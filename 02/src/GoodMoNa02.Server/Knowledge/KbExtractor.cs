using System.Text;
using System.Text.RegularExpressions;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;

namespace GoodMoNa02.Server.Knowledge;

/// <summary>
/// 자료 추출: 붙여넣은 글·파일 내용을 규칙으로 잘라 FAQ·문서 초안으로 만듭니다. AI를 쓰지 않으므로 언제나 같은 결과가 나옵니다.
/// 클라우드 AI(Claude·Codex)로 초기 자료를 정리할 때도 이 형식으로 내보내게 해서 같은 길로 들어옵니다.
/// <code>
/// # 큰 제목              → 문서 제목의 앞부분 (아래 ## 소제목이 있으면 "큰 제목 · 소제목")
/// ## 소제목              → 문서 한 건 (다음 제목 전까지가 본문)
/// Q: 질문 / A: 답         → FAQ 한 건 (질문:/답: 도 됨, 여러 줄 가능)
/// 분류: 예약              → 이후 자료의 분류
/// 키워드: 주차장, 차      → 바로 앞(또는 다음) 자료의 검색 보조어
/// &lt;!-- 메모 --&gt;          → 무시 (예시 자료 표시 등)
/// </code>
/// 제목도 Q/A도 없으면 글 전체가 문서 한 건(제목 = 이름)입니다. 본문이 너무 길면 문단 단위로 나눕니다.
/// </summary>
public static partial class KbExtractor
{
    public static KbImportPreviewDto Extract(string text, string name, string? defaultCategory)
    {
        var warnings = new List<string>();
        var items = new List<KbDraftDto>();
        var category = Clean(defaultCategory, KbLimits.Category) is { Length: > 0 } c ? c : "일반";
        string? h1 = null;
        string? pendingKeywords = null;

        // 지금 모으는 자료
        KbKind? kind = null;
        string title = "";
        var question = new StringBuilder();
        var body = new StringBuilder();
        var inAnswer = false;
        string itemCategory = category;
        string keywords = "";

        void Flush()
        {
            if (kind is null) return;
            if (kind == KbKind.Faq)
            {
                var q = OneLine(question.ToString());
                var a = Tidy(body.ToString());
                if (q.Length == 0) warnings.Add("질문이 비어 있는 FAQ를 건너뛰었습니다.");
                else if (a.Length == 0) warnings.Add($"답이 없는 질문을 건너뛰었습니다: \"{Short(q)}\"");
                else Add(KbKind.Faq, q, a);
            }
            else
            {
                var b = Tidy(body.ToString());
                if (b.Length > 0) Add(KbKind.Doc, title, b);
            }
            kind = null;
            question.Clear();
            body.Clear();
            inAnswer = false;
            keywords = "";
        }

        void Add(KbKind k, string t, string b)
        {
            t = Clip(t, KbLimits.Title);
            var parts = SplitBody(b, KbLimits.Body);
            if (parts.Count > 1) warnings.Add($"본문이 길어 {parts.Count}개로 나눴습니다: \"{Short(t)}\"");
            for (var i = 0; i < parts.Count; i++)
            {
                var pt = parts.Count == 1 ? t : Clip($"{t} ({i + 1}/{parts.Count})", KbLimits.Title);
                items.Add(new KbDraftDto(k, pt, parts[i], itemCategory, Clip(keywords, KbLimits.Keywords)));
            }
        }

        void Start(KbKind k, string t)
        {
            Flush();
            kind = k;
            title = t;
            itemCategory = category;
            keywords = pendingKeywords ?? "";
            pendingKeywords = null;
        }

        text = Comments().Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), "");
        var preamble = new StringBuilder();
        var sawStructure = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            Match m;
            if ((m = Heading().Match(line)).Success)
            {
                sawStructure = true;
                var level = m.Groups[1].Value.Length;
                var heading = Clean(m.Groups[2].Value, KbLimits.Title);
                if (level == 1)
                {
                    Flush();
                    h1 = heading;
                    Start(KbKind.Doc, heading); // 큰 제목 아래 바로 본문이 오면 그 자체로 문서
                }
                else Start(KbKind.Doc, h1 is null ? heading : $"{h1} · {heading}");
            }
            else if ((m = QuestionLine().Match(line)).Success)
            {
                sawStructure = true;
                Start(KbKind.Faq, "");
                question.Append(m.Groups[1].Value).Append(' ');
            }
            else if (kind == KbKind.Faq && !inAnswer && (m = AnswerLine().Match(line)).Success)
            {
                inAnswer = true;
                body.AppendLine(m.Groups[1].Value);
            }
            else if ((m = CategoryLine().Match(line)).Success)
            {
                category = Clean(m.Groups[1].Value, KbLimits.Category) is { Length: > 0 } nc ? nc : "일반";
                if (kind is not null && body.Length == 0 && question.Length == 0) itemCategory = category;
            }
            else if ((m = KeywordLine().Match(line)).Success)
            {
                var kw = string.Join(", ", m.Groups[1].Value.Split([',', '、', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                if (kind is null) pendingKeywords = kw;
                else keywords = kw;
            }
            else if (kind == KbKind.Faq && !inAnswer)
            {
                if (line.Trim().Length > 0) question.Append(line.Trim()).Append(' '); // 여러 줄 질문
            }
            else if (kind is not null) body.AppendLine(line);
            else preamble.AppendLine(line);
        }
        Flush();

        // 제목·Q/A 앞에 있던 글, 또는 구조가 전혀 없는 글 → 이름을 제목으로 한 문서
        var pre = Tidy(preamble.ToString());
        if (pre.Length > 0)
        {
            if (sawStructure && pre.Length < 60) warnings.Add($"제목 없이 짧은 글을 건너뛰었습니다: \"{Short(pre)}\"");
            else
            {
                kind = KbKind.Doc;
                title = name;
                itemCategory = category;
                keywords = "";
                var before = items.Count;
                body.Append(pre);
                Flush();
                // 앞부분 글이므로 맨 앞으로
                var added = items.Skip(before).ToList();
                items.RemoveRange(before, added.Count);
                items.InsertRange(0, added);
            }
        }

        if (items.Count > KbLimits.ImportItems)
        {
            warnings.Add($"자료가 {items.Count}건이라 앞의 {KbLimits.ImportItems}건만 가져옵니다. 나눠서 가져오세요.");
            items = items.Take(KbLimits.ImportItems).ToList();
        }
        if (items.Count == 0) warnings.Add("가져올 자료를 찾지 못했습니다. 제목(#)이나 Q:/A: 형식을 확인해 주세요.");
        return new KbImportPreviewDto(items, warnings);
    }

    /// <summary>본문을 문단 경계에서 max 글자 이하로 나눕니다(한 문단이 더 길면 그 문단을 자름).</summary>
    public static List<string> SplitBody(string body, int max)
    {
        if (body.Length <= max) return [body];
        var parts = new List<string>();
        var cur = new StringBuilder();
        foreach (var para in body.Split("\n\n"))
        {
            var p = para;
            while (p.Length > max)
            {
                if (cur.Length > 0) { parts.Add(cur.ToString().Trim()); cur.Clear(); }
                parts.Add(p[..max].Trim());
                p = p[max..];
            }
            if (cur.Length + p.Length + 2 > max && cur.Length > 0) { parts.Add(cur.ToString().Trim()); cur.Clear(); }
            if (cur.Length > 0) cur.Append("\n\n");
            cur.Append(p);
        }
        if (cur.ToString().Trim().Length > 0) parts.Add(cur.ToString().Trim());
        return parts;
    }

    /// <summary>앞뒤 빈 줄 제거, 빈 줄 3개 이상은 하나로</summary>
    private static string Tidy(string s) => ManyBlank().Replace(s.Replace("\r", ""), "\n\n").Trim();

    private static string OneLine(string s) => Spaces().Replace(s, " ").Trim();

    private static string Clean(string? s, int max) => Clip(OneLine((s ?? "").Replace("**", "")), max);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd();

    private static string Short(string s) => OneLine(s) is var o && o.Length > 30 ? o[..30] + "…" : OneLine(s);

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*(?:[-*]\s+)?(?:\*\*)?(?:Q|질문)\s*\d*\s*[:.)．]\s*(?:\*\*)?\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex QuestionLine();

    [GeneratedRegex(@"^\s*(?:[-*]\s+)?(?:\*\*)?(?:A|답변|답)\s*\d*\s*[:.)．]\s*(?:\*\*)?\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex AnswerLine();

    [GeneratedRegex(@"^\s*(?:분류|카테고리)\s*[:：]\s*(.+)$")]
    private static partial Regex CategoryLine();

    [GeneratedRegex(@"^\s*(?:키워드|검색어)\s*[:：]\s*(.+)$")]
    private static partial Regex KeywordLine();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyBlank();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
