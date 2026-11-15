using System.Text.Json;
using GoodMoNa03.Server.Data;
using GoodMoNa03.Server.Editing;
using GoodMoNa03.Server.Review;
using GoodMoNa03.Server.Writing;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa03.Server.Pipeline;

/// <summary>초안 JSON 칸 읽고 쓰기, 점검 다시 하기, 승인 가능 여부 — 작업자와 API가 같이 씁니다.</summary>
public static class DraftLogic
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T Deserialize<T>(string json) where T : new() =>
        string.IsNullOrWhiteSpace(json) ? new T() : JsonSerializer.Deserialize<T>(json, Json) ?? new T();

    public static List<Cue> Cues(ShortDraft d) => Deserialize<List<Cue>>(d.CuesJson);
    public static List<Cut> Cuts(ShortDraft d) => Deserialize<List<Cut>>(d.CutsJson);
    public static List<string> Strings(string json) => Deserialize<List<string>>(json);
    public static List<Word> Words(SourceVideo s) => Deserialize<List<Word>>(s.WordsJson);

    public static List<string> HashtagList(string hashtags) =>
        hashtags.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>점검 대상 글: 제목 · 훅 · 설명 · 자막 장마다.</summary>
    public static List<CheckText> CheckTexts(ShortDraft d)
    {
        var texts = new List<CheckText> { new("제목", d.Title), new("훅", d.Hook), new("설명", d.Description) };
        texts.AddRange(Cues(d).Select(c => new CheckText($"자막 {c.Id}", c.Text)));
        return texts.Where(t => !string.IsNullOrWhiteSpace(t.Text)).ToList();
    }

    /// <summary>모든 글을 이어 붙인 것(AI 점검·AI 지적 문장 대조용).</summary>
    public static string AllText(ShortDraft d) => string.Join('\n', CheckTexts(d).Select(t => t.Text.Replace('\n', ' ')));

    /// <summary>
    /// 지금 글로 점검을 다시 합니다. 규칙·유사 제목은 새로 계산하고, AI 지적은 <paramref name="aiRisks"/>가 있으면 그것으로,
    /// 없으면 이전 AI 지적 중 해당 문장이 아직 남아 있는 것만 유지합니다.
    /// 같은 지적(출처·규칙·발췌가 같음)이 전에 해소됐다면 해소 기록을 이어받습니다.
    /// </summary>
    public static async Task RecheckAsync(AppDbContext db, ShortDraft d, IReadOnlyList<AiRisk>? aiRisks, CancellationToken ct)
    {
        var old = d.Findings.ToList();
        var fresh = new List<ComplianceFinding>();

        foreach (var c in ComplianceChecker.Check(CheckTexts(d)))
            fresh.Add(New(FindingSource.Rule, c.Where, c.Rule, c.Excerpt, c.Reason, true));

        // 자막 장 경계에 걸친 표현("확실히 / 풍성해질")은 장마다 검사하면 놓침 → 자막을 이어 붙여 한 번 더, 경계에 걸친 것만 더함
        var cueTexts = Cues(d).Select(c => c.Text.Replace('\n', ' ')).ToList();
        var joined = string.Join(' ', cueTexts);
        foreach (var c in ComplianceChecker.Check([new CheckText("자막 장 경계", joined)]))
        {
            if (!cueTexts.Any(t => t.Contains(c.Match, StringComparison.Ordinal)))
                fresh.Add(New(FindingSource.Rule, c.Where, c.Rule, c.Excerpt, c.Reason, true));
        }

        var all = AllText(d);
        if (aiRisks is not null)
        {
            foreach (var r in aiRisks)
                fresh.Add(New(FindingSource.Ai, "AI 점검", "AI 지적", Trim(r.Text, 300), Trim(r.Reason.Length > 0 ? r.Reason : "AI가 확인을 권한 문장입니다.", 300), true));
        }
        else
        {
            foreach (var f in old.Where(f => f.Source == FindingSource.Ai && Contains(all, f.Excerpt)))
                fresh.Add(New(FindingSource.Ai, f.Where, f.Rule, f.Excerpt, f.Reason, f.Blocking));
        }

        if (d.Title.Length > 0)
        {
            var history = await db.Titles.AsNoTracking().Select(t => t.Title).ToListAsync(ct);
            foreach (var (title, score) in TitleSimilarity.Similar(d.Title, history))
                fresh.Add(New(FindingSource.Similar, "제목", "비슷한 기존 제목", Trim(title, 300),
                    $"기존 쇼츠 제목과 {score:P0} 겹칩니다. 같은 약속을 반복하면 넘겨 버리는 비율이 오릅니다.", false));
        }

        foreach (var f in fresh)
        {
            var same = old.FirstOrDefault(o => o.Source == f.Source && o.Rule == f.Rule && o.Excerpt == f.Excerpt && o.Resolution is not null);
            if (same is null) continue;
            f.Resolution = same.Resolution;
            f.ResolvedByName = same.ResolvedByName;
            f.ResolvedAt = same.ResolvedAt;
        }

        db.Findings.RemoveRange(old);
        d.Findings.Clear();
        d.Findings.AddRange(fresh);
    }

    private static bool Contains(string all, string excerpt) =>
        all.Replace(" ", "").Contains(excerpt.Trim('…').Replace(" ", ""), StringComparison.Ordinal);

    private static ComplianceFinding New(FindingSource source, string where, string rule, string excerpt, string reason, bool blocking) =>
        new() { Source = source, Where = Trim(where, 30), Rule = Trim(rule, 30), Excerpt = excerpt, Reason = reason, Blocking = blocking };

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>승인을 막는 이유 목록(비어 있으면 승인 가능).</summary>
    public static List<string> ApprovalBlockers(ShortDraft d)
    {
        var reasons = new List<string>();
        if (d.Status != DraftStatus.Review) reasons.Add("검토 대기 상태가 아닙니다.");
        if (string.IsNullOrWhiteSpace(d.Title)) reasons.Add("제목이 비어 있습니다.");
        var open = d.Findings.Count(f => f.Blocking && f.Resolution is null);
        if (open > 0) reasons.Add($"해소하지 않은 표현 점검 {open}건이 있습니다.");
        if (d.HasPatient && !d.ConsentConfirmed) reasons.Add("환자 출연 영상은 출연 동의 확인이 필요합니다.");
        return reasons;
    }

    /// <summary>자막(원본 기준) → 편집본 기준으로 옮기고 ASS 파일 글을 만듭니다.</summary>
    public static (string Ass, int DurationMs, IReadOnlyList<TimeRange> Keeps) BuildAss(
        ShortDraft d, SourceVideo s, string? endCardText, double endCardSeconds, string fontName)
    {
        var keeps = CutPlanner.Keeps(s.DurationMs, Cuts(d));
        var duration = CutPlanner.OutputDuration(keeps);
        var cues = new List<AssWriter.OutputCue>();
        foreach (var c in Cues(d))
        {
            if (TimeMap.ToOutput(c.StartMs, c.EndMs, keeps) is { } t)
                cues.Add(new AssWriter.OutputCue(t.StartMs, t.EndMs, c.Text));
        }
        var ass = AssWriter.Write(cues, d.Hook, endCardText, d.Code, duration, endCardSeconds, fontName, Strings(d.EmphasisJson));
        return (ass, duration, keeps);
    }
}
