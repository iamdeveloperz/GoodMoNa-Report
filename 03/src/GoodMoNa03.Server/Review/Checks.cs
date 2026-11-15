using System.Text.RegularExpressions;

namespace GoodMoNa03.Server.Review;

/// <summary>점검 대상 글 한 덩어리. Where는 화면 표시용 위치("제목", "훅", "자막 3", "설명").</summary>
public sealed record CheckText(string Where, string Text);

/// <summary>점검 결과 후보. Blocking이면 해소(사유 입력)해야 승인할 수 있습니다.</summary>
/// <summary>점검 결과 후보. Blocking이면 해소(사유 입력)해야 승인할 수 있습니다. Match는 규칙에 걸린 말 그대로.</summary>
public sealed record FindingCandidate(string Where, string Rule, string Excerpt, string Reason, bool Blocking, string Match = "");

/// <summary>
/// 올리기 전 표현 점검 1단계: 규칙 사전. 분석 보고서 S6 점검표를 정규식으로 옮긴 것입니다.
/// 걸렸다고 위반이라는 뜻은 아닙니다 — 사람이 보고 "문제없음 + 사유" 또는 수정으로 해소합니다.
/// </summary>
public static partial class ComplianceChecker
{
    private sealed record Rule(string Name, Regex Pattern, string Reason);

    private static readonly Rule[] Rules =
    [
        new("최상급", Superlative(), "최상급·유일 표현은 객관적 근거가 없으면 의료광고 위반 소지가 있습니다."),
        new("효과 보장", Guarantee(), "치료 효과를 단정·보장하는 표현입니다."),
        new("근거 수치", Number(), "효과 배수·퍼센트는 화면에 출처가 있어야 합니다."),
        new("부작용 부정", NoSideEffect(), "부작용이 없다는 표현은 쓸 수 없습니다."),
        new("과장", Exaggeration(), "과장 표현입니다."),
        new("비교·비방", Disparage(), "다른 치료법·의료기관을 깎아내리는 표현인지 확인하세요."),
        new("경험담·전후", Testimonial(), "환자 사례·전후 사진은 출연 동의와 심의 여부를 확인하세요."),
    ];

    public static List<FindingCandidate> Check(IEnumerable<CheckText> texts)
    {
        var found = new List<FindingCandidate>();
        foreach (var t in texts)
        {
            var text = t.Text.Replace('\n', ' ');
            foreach (var rule in Rules)
            {
                foreach (Match m in rule.Pattern.Matches(text))
                    found.Add(new FindingCandidate(t.Where, rule.Name, Excerpt(text, m.Index, m.Length), rule.Reason, true, m.Value));
            }
        }
        return found;
    }

    /// <summary>걸린 말 앞뒤 몇 글자까지 보여 줍니다.</summary>
    public static string Excerpt(string text, int index, int length)
    {
        var start = Math.Max(0, index - 8);
        var end = Math.Min(text.Length, index + length + 8);
        return (start > 0 ? "…" : "") + text[start..end].Trim() + (end < text.Length ? "…" : "");
    }

    [GeneratedRegex(@"최고|최초|유일|국내\s*1위|넘버\s*원|No\.?\s*1|가장\s*(좋|빠르|확실|효과|안전)|현존\s*최|역사상\s*최|최대\s*(효과|규모)", RegexOptions.IgnoreCase)]
    private static partial Regex Superlative();

    [GeneratedRegex(@"완치|100\s*%|백\s*퍼센트|무조건|반드시\s*(낫|자라|나|효과)|확실(한|하게)\s*(효과|방법|치료)|확실히\s*(더\s*)?(낫|나아|좋아|효과|풍성|자라|굵어)|보장")]
    private static partial Regex Guarantee();

    [GeneratedRegex(@"\d+(\.\d+)?\s*배|\d{2,}(\.\d+)?\s*(%|퍼센트|프로)")]
    private static partial Regex Number();

    [GeneratedRegex(@"부작용\s*(이|은|도)?\s*(없|전혀|zero|제로|0\b)", RegexOptions.IgnoreCase)]
    private static partial Regex NoSideEffect();

    [GeneratedRegex(@"노벨|기적|혁명|마법|신의\s*한\s*수")]
    private static partial Regex Exaggeration();

    [GeneratedRegex(@"사기|농약|돌팔이|엉터리|병원.{0,6}(실수|망하|속이)|그만\s*(속|당하)")]
    private static partial Regex Disparage();

    [GeneratedRegex(@"전후|비포|애프터|후기|경험담|환자.{0,6}(사진|사례)|웨딩\s*사진")]
    private static partial Regex Testimonial();
}

/// <summary>
/// 기존 쇼츠 제목과 비슷한지(반복 피로, 분석 보고서 P5). 공백을 뺀 글자 2개 묶음의 겹침 비율(Dice).
/// 02 CS 도우미 검색이 2-gram을 쓰는 것과 같은 이유: 한국어는 띄어쓰기·조사가 달라도 2-gram이 잘 겹칩니다.
/// </summary>
public static class TitleSimilarity
{
    public const double Threshold = 0.5;

    public static double Score(string a, string b)
    {
        var x = Bigrams(a);
        var y = Bigrams(b);
        if (x.Count == 0 || y.Count == 0) return 0;
        var common = 0;
        var pool = new Dictionary<string, int>();
        foreach (var g in y) pool[g] = pool.GetValueOrDefault(g) + 1;
        foreach (var g in x)
        {
            if (pool.TryGetValue(g, out var n) && n > 0)
            {
                common++;
                pool[g] = n - 1;
            }
        }
        return 2.0 * common / (x.Count + y.Count);
    }

    public static List<string> Bigrams(string text)
    {
        var t = new string(text.Where(c => char.IsLetterOrDigit(c)).ToArray()).ToLowerInvariant();
        var list = new List<string>();
        for (var i = 0; i + 1 < t.Length; i++) list.Add(t.Substring(i, 2));
        return list;
    }

    /// <summary>점수가 기준 이상인 기존 제목(높은 순, 최대 3개).</summary>
    public static List<(string Title, double Score)> Similar(string title, IEnumerable<string> history) =>
        history.Select(h => (Title: h, Score: Score(title, h)))
            .Where(x => x.Score >= Threshold)
            .OrderByDescending(x => x.Score)
            .Take(3)
            .ToList();
}
