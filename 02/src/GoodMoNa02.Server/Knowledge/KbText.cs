using System.Text;
using System.Text.RegularExpressions;

namespace GoodMoNa02.Server.Knowledge;

/// <summary>
/// 검색용 글자 처리. 한국어는 조사가 붙어("환불은", "주차장에") 낱말 단위로는 잘 안 맞으므로,
/// 낱말을 <b>두 글자씩(2-gram)</b> 잘라 비교합니다. "환불은" → 환불·불은, "환불" → 환불 → 일치.
/// (SQLite FTS5 trigram은 세 글자 이상만 찾아 "연차·환불·주차" 같은 두 글자 낱말을 놓칩니다.)
/// </summary>
public static partial class KbText
{
    /// <summary>질문 끝말에서 흔히 생기는 조각: 질문 쪽에서만 빼서 "일치 비율"이 낮아지지 않게 합니다.</summary>
    private static readonly HashSet<string> QueryNoise =
    [
        "나요", "가요", "까요", "어요", "해요", "세요", "니까", "습니", "합니", "는지", "인가", "되나", "있나", "을까", "할까", "데요",
        "건가", "던가", "려면", "으면", "하면", "알려", "려줘", "주세", "줘요", "뭐야", "언제", "어디", "어떻", "떻게", "얼마", "몇시", "예요", "이에",
    ];

    /// <summary>글을 소문자·글자/숫자 낱말로 나눈 뒤 2-gram 목록으로 (한 글자 낱말은 그대로)</summary>
    public static List<string> Tokens(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;
        var word = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant().Append(' '))
        {
            if (char.IsLetterOrDigit(ch))
            {
                word.Append(ch);
                continue;
            }
            if (word.Length == 1) tokens.Add(word.ToString());
            for (var i = 0; i + 1 < word.Length; i++) tokens.Add(word.ToString(i, 2));
            word.Clear();
        }
        return tokens;
    }

    /// <summary>질문의 검색 조각 (중복·질문 끝말 제외). 다 빠지면 원래 조각을 씁니다.</summary>
    public static List<string> QueryTokens(string question)
    {
        var all = Tokens(question).Distinct().ToList();
        var useful = all.Where(t => !QueryNoise.Contains(t)).ToList();
        return useful.Count > 0 ? useful : all;
    }

    /// <summary>
    /// 질문 기록에 남기기 전에 전화번호·주민등록번호를 가립니다.
    /// CS 질문에 환자 연락처가 섞여 들어와도 DB와 AI에 원문이 남지 않게 합니다.
    /// </summary>
    public static string MaskPii(string text)
    {
        text = ResidentNumber().Replace(text, "******-*******");
        text = PhoneNumber().Replace(text, m => $"{m.Groups[1].Value}-****-****");
        return text;
    }

    [GeneratedRegex(@"(?<!\d)\d{6}\s*-?\s*[1-8]\d{6}(?!\d)")]
    private static partial Regex ResidentNumber();

    [GeneratedRegex(@"(?<!\d)(01[016789]|0\d{1,2})[\s.-]?\d{3,4}[\s.-]?\d{4}(?!\d)")]
    private static partial Regex PhoneNumber();
}
