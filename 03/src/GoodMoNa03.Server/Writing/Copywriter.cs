using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GoodMoNa03.Server.Infrastructure;
using Microsoft.Extensions.Options;

namespace GoodMoNa03.Server.Writing;

/// <summary>초안 글: 훅 3안 · 제목 3안 · 요약 · 해시태그 · 강조 단어. AiNote는 AI를 못 쓴 이유(화면 안내용).</summary>
public sealed record CopyDraft(
    IReadOnlyList<string> Hooks,
    IReadOnlyList<string> Titles,
    string Summary,
    IReadOnlyList<string> Hashtags,
    IReadOnlyList<string> Emphasis,
    string? AiNote);

/// <summary>AI가 찾은 표현 위험 문장.</summary>
public sealed record AiRisk(string Text, string Reason);

/// <summary>
/// 받아쓰기 → 제목·훅 초안. AI가 꺼져 있거나 실패하면 규칙으로 만든 초안(첫 문장 = 제목)을 씁니다.
/// AI 답은 그대로 믿지 않고 길이·개수·금지 표현을 서버에서 다시 거릅니다.
/// </summary>
public sealed partial class Copywriter(IAiWriter ai, IOptions<AppOptions> options, ILogger<Copywriter> log)
{
    private readonly CopyOptions _o = options.Value.Copy;

    public const string System = """
        당신은 탈모 치료·모발이식 병원 원장이 직접 출연하는 유튜브 쇼츠의 편집 담당입니다.
        원장이 말한 자막(받아쓰기)만 근거로, 쇼츠 첫 화면 위쪽에 띄울 훅 문구와 영상 제목을 만듭니다.

        규칙:
        - 자막에 없는 사실·수치·효과를 지어내지 않습니다.
        - 의료광고 규정을 지킵니다: 최고·최초·유일·완치·100%·N배·"부작용 없음" 같은 단정·과장 표현, 다른 치료나 병원을 깎아내리는 표현, 환자 경험담 표현을 쓰지 않습니다.
        - 공포를 주기보다 구체적인 정보로 궁금하게 만듭니다. 숫자 목록("3가지"), 구체적 상황, 의사 본인의 경험이 효과적입니다.
        - 훅: 15자 이내 3개. 제목: 40자 이내 3개(서로 다른 각도). 요약: 2문장. 해시태그: 3~5개(#으로 시작).
        - 강조 단어: 자막 안에서 화면에 강조색으로 보일 핵심 단어 3~6개(자막에 실제로 나온 단어만).

        반드시 다음 JSON 하나만 답합니다:
        {"hooks":["..."],"titles":["..."],"summary":"...","hashtags":["#..."],"emphasis":["..."]}
        """;

    public const string RiskSystem = """
        당신은 병원 유튜브 쇼츠의 의료광고 표현을 점검합니다. 의료법 제56조 취지에 비추어 문제가 될 수 있는 문장을 찾습니다:
        치료 효과를 단정하거나 보장하는 표현, 근거가 제시되지 않은 수치, 다른 치료법·의료기관 비교·비방, 환자 치료 경험담,
        부작용이 없다는 표현, 과장(최고·기적 등).
        문제가 없으면 빈 목록을 답합니다. 반드시 다음 JSON 하나만 답합니다:
        {"risks":[{"text":"문제 문장 그대로","reason":"짧은 이유"}]}
        """;

    public async Task<CopyDraft> DraftAsync(string transcript, CancellationToken ct)
    {
        var fallback = Fallback(transcript);
        if (!ai.Enabled) return fallback with { AiNote = "AI 꺼짐 — 첫 문장으로 제목 초안을 만들었습니다." };
        try
        {
            var raw = await ai.CompleteJsonAsync(System, $"자막:\n{transcript}", ct);
            var parsed = Parse(raw);
            if (parsed is null) return fallback with { AiNote = "AI 답을 읽지 못해 규칙 초안을 씁니다." };
            return Merge(parsed, fallback, transcript);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(e, "제목·훅 초안 AI 실패 — 규칙 초안 사용");
            return fallback with { AiNote = $"AI 초안 실패({ai.Label}) — 규칙 초안을 씁니다." };
        }
    }

    public async Task<IReadOnlyList<AiRisk>> ReviewAsync(string text, CancellationToken ct)
    {
        if (!ai.Enabled) return [];
        try
        {
            var raw = await ai.CompleteJsonAsync(RiskSystem, text, ct);
            using var doc = JsonDocument.Parse(ExtractJson(raw));
            if (!doc.RootElement.TryGetProperty("risks", out var risks) || risks.ValueKind != JsonValueKind.Array) return [];
            return risks.EnumerateArray()
                .Select(r => new AiRisk(Str(r, "text"), Str(r, "reason")))
                .Where(r => r.Text.Length > 0)
                .Take(10).ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(e, "AI 표현 점검 실패 — 규칙 점검만 사용");
            return [];
        }
    }

    /// <summary>AI 없이: 첫 문장을 제목으로, 기본 해시태그.</summary>
    public CopyDraft Fallback(string transcript)
    {
        var sentences = SentencePattern().Split(transcript).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var first = sentences.FirstOrDefault() ?? "";
        var title = Clip(first.TrimEnd('.', '。'), _o.MaxTitleChars); // 제목은 마침표 없이
        var summary = string.Join(' ', sentences.Take(2));
        return new CopyDraft([], title.Length > 0 ? [title] : [], Clip(summary, 200), _o.DefaultHashtags, [], null);
    }

    private sealed record Parsed(List<string> Hooks, List<string> Titles, string Summary, List<string> Hashtags, List<string> Emphasis);

    private static Parsed? Parse(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(raw));
            var r = doc.RootElement;
            return new Parsed(List(r, "hooks"), List(r, "titles"), Str(r, "summary"), List(r, "hashtags"), List(r, "emphasis"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private CopyDraft Merge(Parsed p, CopyDraft fallback, string transcript)
    {
        var hooks = p.Hooks.Select(h => Clip(h, 20)).Where(h => h.Length > 0).Distinct().Take(3).ToList();
        var titles = p.Titles.Select(t => Clip(t, _o.MaxTitleChars)).Where(t => t.Length > 0).Distinct().Take(3).ToList();
        if (titles.Count == 0) titles = [.. fallback.Titles];
        var tags = p.Hashtags.Select(NormalizeTag).Where(t => t.Length > 1).Distinct().Take(5).ToList();
        if (tags.Count == 0) tags = [.. fallback.Hashtags];
        // 강조 단어는 자막에 실제로 있는 것만
        var emphasis = p.Emphasis.Select(e => e.Trim()).Where(e => e.Length >= 2 && transcript.Contains(e, StringComparison.Ordinal)).Distinct().Take(6).ToList();
        var summary = p.Summary.Length > 0 ? Clip(p.Summary, 200) : fallback.Summary;
        return new CopyDraft(hooks, titles, summary, tags, emphasis, null);
    }

    /// <summary>AI가 코드 블록(```json)이나 앞뒤 설명을 붙여도 JSON 부분만.</summary>
    public static string ExtractJson(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : raw;
    }

    private static List<string> List(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).ToList()
            : [];

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()!.Trim() : "";

    private static string NormalizeTag(string tag)
    {
        var t = WhitespacePattern().Replace(tag.Trim().TrimStart('#'), "");
        return t.Length == 0 ? "" : "#" + t;
    }

    public static string Clip(string text, int max)
    {
        var t = WhitespacePattern().Replace(text.Replace('\n', ' '), " ").Trim().Trim('"', '“', '”', '\'');
        return t.Length <= max ? t : t[..max].TrimEnd();
    }

    [GeneratedRegex(@"(?<=[.?!])\s+")]
    private static partial Regex SentencePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}

/// <summary>영상 코드: S + 월일 + 그날 순번 글자(S1116A, S1116B …). 상담 접수 때 "어떤 영상 보고 오셨나요?"에 쓰입니다.</summary>
public static class ShortCode
{
    public static string For(DateOnly day, int indexInDay)
    {
        var letters = new StringBuilder();
        var n = indexInDay;
        do
        {
            letters.Insert(0, (char)('A' + n % 26));
            n = n / 26 - 1;
        } while (n >= 0);
        return $"S{day:MMdd}{letters}";
    }

    public static string Prefix(DateOnly day) => $"S{day:MMdd}";
}

/// <summary>설명란 = 템플릿 + 요약 + 영상 코드 + (선택) UTM 붙은 상담 주소 + 해시태그.</summary>
public static class DescriptionBuilder
{
    public static string Build(string template, string summary, string code, string landingUrl, IEnumerable<string> hashtags) =>
        template
            .Replace("{summary}", summary)
            .Replace("{code}", code)
            .Replace("{landingUrl}", WithUtm(landingUrl, code))
            .Replace("{hashtags}", string.Join(' ', hashtags))
            .Trim();

    public static string WithUtm(string url, string code)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var sep = url.Contains('?') ? '&' : '?';
        return $"{url}{sep}utm_source=youtube&utm_medium=shorts&utm_campaign={Uri.EscapeDataString(code)}";
    }
}
