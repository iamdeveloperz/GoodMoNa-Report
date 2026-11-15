using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoodMoNa02.Server.Knowledge;

/// <summary>
/// 자동 정리: 사람이 마지막에 승인만 하면 되도록 초안·제안을 미리 만들어 둡니다. 자료를 직접 바꾸지는 않습니다.
/// ① 직원 정정 → FAQ 초안 (AI가 있으면 안내 문장으로 다듬음, 사실은 더하지 않음)
/// ② 못 찾은 질문 → 답이 되는 기존 자료를 골라 "표현 추가" 제안
/// ③ 병원 홈페이지 → 바뀐 줄을 찾아 "자료 고치기" 제안 (AI가 있으면 고친 본문까지)
/// 매일 새벽(App:Knowledge:CurateHour)에 전부, 직원이 정정을 보내면 ①만 바로 돕니다. 관리자는 "지금 정리하기"로도 돌립니다.
/// </summary>
public sealed partial class KbCurator(
    IServiceScopeFactory scopes, IHttpClientFactory http, KbIndex index, IAnswerWriter writer, ClinicClock clock, ILogger<KbCurator> log)
{
    public const string HttpClientName = "site-watch";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>마지막 전체 정리 결과 (서버를 다시 켜면 비어 있음)</summary>
    public KbCurateResultDto? LastRun { get; private set; }

    /// <summary>직원이 정정을 보냄 → 백그라운드에서 곧 ①을 돌림</summary>
    public void RequestCorrections()
    {
        try { _signal.Release(); } catch (SemaphoreFullException) { } // 이미 요청됨
    }

    internal Task<bool> WaitForRequestAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);

    /// <summary>정리 한 번. 이미 도는 중이면 null (겹쳐 돌지 않음)</summary>
    public async Task<KbCurateResultDto?> TryRunAsync(bool full, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return null;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var errors = new List<string>();
            var drafts = await DraftsFromCorrectionsAsync(db, errors, ct);
            int keywords = 0, skipped = 0, pages = 0, changes = 0;
            if (full)
            {
                (pages, changes) = await CheckSitesAsync(db, errors, ct);
                (keywords, skipped) = await SuggestKeywordsAsync(db, errors, ct);
            }
            var result = new KbCurateResultDto(clock.UtcNow, full, drafts, keywords, pages, changes, skipped, errors.Take(20).ToList());
            if (full) LastRun = result;
            if (drafts + keywords + changes > 0 || errors.Count > 0)
                log.LogInformation("자동 정리: 초안 {Drafts} · 표현 제안 {Keywords} · 홈페이지 {Pages}쪽 중 변경 {Changes} · 오류 {Errors}", drafts, keywords, pages, changes, errors.Count);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /* ---------- ① 직원 정정 → FAQ 초안 ---------- */

    private async Task<int> DraftsFromCorrectionsAsync(AppDbContext db, List<string> errors, CancellationToken ct)
    {
        var logs = await db.AskLogs
            .Where(a => a.FollowUp == AskFollowUp.Open && a.Correction != null && a.DraftItemId == null)
            .OrderBy(a => a.Id).Take(50).ToListAsync(ct);
        foreach (var l in logs)
        {
            var body = l.Correction!.Trim();
            if (writer.Status.Enabled)
            {
                try
                {
                    var polished = (await writer.CompleteAsync(CuratePrompt.Polish, CuratePrompt.PolishUser(l.Question, body), ct)).Trim();
                    if (polished.Length is > 0 and <= KbLimits.Body && !polished.Contains(AnswerPrompt.NotFound)) body = polished;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    errors.Add($"정정 다듬기 실패(질문 #{l.Id}): {e.Message}"); // 다듬지 못해도 직원이 쓴 그대로 초안을 만듦
                }
            }
            var category = l.TopItemId is { } top
                ? await db.KbItems.Where(i => i.Id == top).Select(i => i.Category).FirstOrDefaultAsync(ct) ?? "일반"
                : "일반";
            var item = new KbItem
            {
                Kind = KbKind.Faq, Status = KbStatus.Draft, Title = Clip(l.Question.Trim(), KbLimits.Title), Body = Clip(body, KbLimits.Body),
                Category = category, Source = KbSource.Question, SourceRef = $"질문 #{l.Id} · 직원 정정",
                CreatedAt = clock.UtcNow, CreatedById = l.StaffId, CreatedByName = l.StaffName,
            };
            db.KbItems.Add(item);
            await db.SaveChangesAsync(ct);
            l.DraftItemId = item.Id;
            l.CuratedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return logs.Count;
    }

    /* ---------- ② 못 찾은 질문 → 기존 자료에 표현 추가 제안 ---------- */

    private async Task<(int Suggestions, int Skipped)> SuggestKeywordsAsync(AppDbContext db, List<string> errors, CancellationToken ct)
    {
        var logs = await db.AskLogs
            .Where(a => a.FollowUp == AskFollowUp.Open && a.Correction == null && a.CuratedAt == null)
            .OrderBy(a => a.Id).Take(100).ToListAsync(ct);
        var groups = new Dictionary<int, List<AskLog>>();
        var skipped = 0;
        foreach (var l in logs)
        {
            l.CuratedAt = clock.UtcNow;
            if (Phrase(l.Question) is not { } _) { skipped++; continue; }
            var hits = await index.SearchAsync(db, l.Question, 4, ct);
            int? pick = null;
            if (hits.Count > 0 && writer.Status.Enabled)
            {
                try
                {
                    var raw = await writer.CompleteAsync(CuratePrompt.Pick, CuratePrompt.PickUser(l.Question, hits), ct);
                    if (FirstNumber().Match(raw) is { Success: true } m && int.TryParse(m.Value, out var n) && n >= 1 && n <= hits.Count) pick = hits[n - 1].Id;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    errors.Add($"자료 고르기 실패(질문 #{l.Id}): {e.Message}");
                    l.CuratedAt = null; // 다음 정리 때 다시
                    continue;
                }
            }
            else if (hits.Count > 0 && hits[0].Coverage >= 0.3) pick = hits[0].Id; // AI가 꺼져 있으면 많이 겹칠 때만
            // 답할 때 AI가 "자료에 없음"이라고 한 자료를 다시 고르면 믿지 않음
            if (pick is null || (l.AiDeclined && pick == l.TopItemId)) { skipped++; continue; }
            (groups.TryGetValue(pick.Value, out var list) ? list : groups[pick.Value] = []).Add(l);
        }
        await db.SaveChangesAsync(ct);

        var made = 0;
        foreach (var (itemId, group) in groups)
        {
            var item = await db.KbItems.FirstAsync(i => i.Id == itemId, ct);
            var phrases = group.Select(g => Phrase(g.Question)!).Distinct()
                .Where(p => !item.Keywords.Contains(p, StringComparison.Ordinal)).ToList();
            var pending = await db.KbSuggestions.FirstOrDefaultAsync(
                s => s.Status == KbSuggestionStatus.Pending && s.Kind == KbSuggestionKind.Keywords && s.ItemId == itemId, ct);
            var ids = group.Select(g => g.Id.ToString()).ToList();
            if (pending is null)
            {
                pending = new KbSuggestion
                {
                    Kind = KbSuggestionKind.Keywords, Status = KbSuggestionStatus.Pending, ItemId = itemId, Title = "",
                    Detail = "이 자료로 답할 수 있는데 표현이 달라 못 찾은 질문입니다. 반영하면 다음부터 이 표현으로도 찾습니다.",
                    CreatedAt = clock.UtcNow,
                };
                db.KbSuggestions.Add(pending);
                made++;
            }
            var allPhrases = Lines(pending.Phrases).Union(phrases).ToList();
            var allIds = pending.AskLogIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Union(ids).ToList();
            pending.Phrases = Clip(string.Join('\n', allPhrases), 1000);
            pending.AskLogIds = Clip(string.Join(',', allIds), 1000);
            pending.Title = Clip($"「{item.Title}」에 표현 {allPhrases.Count}개 추가", KbLimits.Title);
        }
        await db.SaveChangesAsync(ct);
        return (made, skipped);
    }

    /// <summary>검색 보조어로 붙일 표현 (질문 끝 부호 뺌, 60자 넘으면 붙이지 않음)</summary>
    public static string? Phrase(string question)
    {
        var p = question.Trim().TrimEnd('?', '？', '.', '!', '~').Trim();
        return p.Length is >= 2 and <= 60 ? p : null;
    }

    /* ---------- ③ 병원 홈페이지 → 바뀐 내용 확인 ---------- */

    private async Task<(int Checked, int Changes)> CheckSitesAsync(AppDbContext db, List<string> errors, CancellationToken ct)
    {
        var pages = await db.KbWatchPages.OrderBy(p => p.Id).ToListAsync(ct);
        var client = http.CreateClient(HttpClientName);
        var changes = 0;
        for (var n = 0; n < pages.Count; n++)
        {
            var page = pages[n];
            if (n > 0) await Task.Delay(TimeSpan.FromSeconds(1), ct); // 병원 서버에 한꺼번에 요청하지 않게
            try
            {
                using var res = await client.GetAsync(page.Url, ct);
                res.EnsureSuccessStatusCode();
                var lines = PageText.Lines(await res.Content.ReadAsStringAsync(ct));
                var text = Clip(string.Join('\n', lines), KbLimits.PageText);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                if (page.LastHash is not null && hash != page.LastHash)
                {
                    var before = Lines(page.LastText).ToHashSet();
                    var after = lines.ToHashSet();
                    var removed = Lines(page.LastText).Where(l => !after.Contains(l)).ToList();
                    var added = lines.Where(l => !before.Contains(l)).ToList();
                    if (removed.Count + added.Count > 0)
                    {
                        await SuggestSiteChangeAsync(db, page.Url, removed, added, errors, ct);
                        changes++;
                    }
                    page.LastChangedAt = clock.UtcNow;
                }
                // 처음 읽은 내용은 기준으로만 저장 (이미 가져온 자료와 비교하지 않음)
                (page.LastHash, page.LastText, page.LastError) = (hash, text, null);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                page.LastError = Clip(e.Message, 300);
                errors.Add($"{page.Url}: {e.Message}");
            }
            page.LastCheckedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return (pages.Count, changes);
    }

    private async Task SuggestSiteChangeAsync(AppDbContext db, string url, List<string> removed, List<string> added, List<string> errors, CancellationToken ct)
    {
        var detail = new StringBuilder();
        if (removed.Count > 0) detail.Append("홈페이지에서 사라진 줄:\n").AppendJoin('\n', removed.Take(40).Select(l => "- " + l)).Append('\n');
        if (added.Count > 0) detail.Append("새로 생긴 줄:\n").AppendJoin('\n', added.Take(40).Select(l => "+ " + l));
        var changed = string.Join(' ', removed.Concat(added).Take(20));
        var hits = await index.SearchAsync(db, Clip(changed, KbLimits.Question), 3, ct);

        int? itemId = null;
        string? proposed = null;
        if (writer.Status.Enabled)
        {
            foreach (var h in hits)
            {
                try
                {
                    var raw = (await writer.CompleteAsync(CuratePrompt.SiteChange, CuratePrompt.SiteChangeUser(h, removed, added), ct)).Trim();
                    if (raw.Length is 0 or > KbLimits.Body || raw.Contains(CuratePrompt.NoChange) || raw == h.Body.Trim()) continue;
                    (itemId, proposed) = (h.Id, raw);
                    break;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    errors.Add($"홈페이지 변경 반영 실패({url}): {e.Message}");
                    break;
                }
            }
        }
        else if (hits.Count > 0 && hits[0].Coverage >= 0.3) itemId = hits[0].Id; // 고칠 본문은 관리자가 직접

        var itemTitle = itemId is null ? null : hits.First(h => h.Id == itemId).Title;
        db.KbSuggestions.Add(new KbSuggestion
        {
            Kind = KbSuggestionKind.SiteChange, Status = KbSuggestionStatus.Pending, ItemId = itemId,
            Title = Clip(itemTitle is null ? $"홈페이지 변경: {ShortUrl(url)}" : $"홈페이지 변경 → 「{itemTitle}」 고치기", KbLimits.Title),
            Detail = Clip(detail.ToString(), KbLimits.Body), ProposedBody = proposed, SourceUrl = url, CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    private static string ShortUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host + u.AbsolutePath : url;

    public static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"\d+")]
    private static partial Regex FirstNumber();

    /// <summary>
    /// 확인할 홈페이지 주소 검사: http(s)만, 서버 PC 자신·내부망 주소는 안 됨(외부 공개 홈페이지만).
    /// </summary>
    public static string? CheckWatchUrl(string? url, out string error)
    {
        error = "";
        var raw = (url ?? "").Trim();
        if (raw.Length is 0 or > KbLimits.Url || !Uri.TryCreate(raw, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
        {
            error = $"http:// 또는 https://로 시작하는 주소를 {KbLimits.Url}자 안으로 써 주세요.";
            return null;
        }
        if (u.IsLoopback || IPAddress.TryParse(u.Host, out _) || !u.Host.Contains('.') || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            error = "병원 공개 홈페이지 주소(도메인)만 등록할 수 있습니다.";
            return null;
        }
        return u.GetLeftPart(UriPartial.Query);
    }
}

/// <summary>자동 정리에 쓰는 AI 지시. 답변 지시(AnswerPrompt)처럼 "없는 사실을 더하지 않기"가 핵심입니다.</summary>
public static class CuratePrompt
{
    public const string NoChange = "변경 없음";

    public const string Polish =
        "당신은 굿모나의원 CS 자료를 정리하는 도우미입니다.\n" +
        "직원이 적은 정정 내용을 고객에게 그대로 안내할 수 있는 FAQ 답으로 다듬습니다.\n" +
        "규칙:\n" +
        "1. 정정 내용에 없는 사실(가격·시간·기간·효과·조건)을 더하지 않습니다.\n" +
        "2. 한국어 존댓말 1~4문장. 답만 씁니다(제목·머리말·따옴표 없이).";

    public static string PolishUser(string question, string correction) => $"[고객 질문]\n{question}\n\n[직원 정정]\n{correction}";

    public const string Pick =
        "고객 질문에 답하는 자료 번호를 고릅니다.\n" +
        "자료 내용만으로 그 질문에 직접 답할 수 있을 때만 그 번호를 고르고, 아니면 0을 고릅니다.\n" +
        "숫자 하나만 씁니다.";

    public static string PickUser(string question, IReadOnlyList<KbHit> hits)
    {
        var sb = new StringBuilder($"[질문]\n{question}\n\n[자료]\n");
        for (var i = 0; i < hits.Count; i++)
        {
            var body = hits[i].Body.Length > 600 ? hits[i].Body[..600] + "…" : hits[i].Body;
            sb.Append($"[{i + 1}] {hits[i].Title}\n{body}\n\n");
        }
        return sb.ToString();
    }

    public const string SiteChange =
        "병원 홈페이지 내용이 바뀌었습니다. [기존 자료]를 [사라진 줄]·[새로 생긴 줄]에 맞게 고친 본문을 씁니다.\n" +
        "규칙:\n" +
        "1. 바뀐 줄이 이 자료의 내용과 관계없으면 정확히 \"" + NoChange + "\"이라고만 씁니다.\n" +
        "2. 바뀐 줄에 없는 사실을 더하지 않고, 바뀌지 않은 문장은 그대로 둡니다.\n" +
        "3. 고친 본문만 씁니다(설명·머리말 없이).";

    public static string SiteChangeUser(KbHit item, IReadOnlyList<string> removed, IReadOnlyList<string> added) =>
        $"[기존 자료] {item.Title}\n{item.Body}\n\n[사라진 줄]\n{string.Join('\n', removed.Take(40))}\n\n[새로 생긴 줄]\n{string.Join('\n', added.Take(40))}";
}

/// <summary>HTML → 본문 글자 줄 (스크립트·스타일 빼고, 같은 줄은 한 번만)</summary>
public static partial class PageText
{
    public static List<string> Lines(string html)
    {
        var s = NonText().Replace(html, " ");
        s = BlockEnd().Replace(s, "\n");
        s = Tag().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        return s.Split('\n')
            .Select(l => Spaces().Replace(l, " ").Trim())
            .Where(l => l.Length > 1)
            .Distinct()
            .ToList();
    }

    [GeneratedRegex(@"<(script|style|noscript|template|svg)\b[\s\S]*?</\1\s*>|<!--[\s\S]*?-->", RegexOptions.IgnoreCase)]
    private static partial Regex NonText();

    [GeneratedRegex(@"<(br|/p|/div|/li|/h[1-6]|/tr|/section|/article|/td|/th)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>매일 새벽 전체 정리 + 직원 정정이 오면 바로 초안 만들기 (테스트 환경에서는 켜지 않음)</summary>
public sealed class KbCuratorHost(KbCurator curator, ClinicClock clock, IOptions<AppOptions> options, ILogger<KbCuratorHost> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hour = Math.Clamp(options.Value.Knowledge.CurateHour, 0, 23);
        while (!stoppingToken.IsCancellationRequested)
        {
            var local = clock.ToLocal(clock.UtcNow);
            var date = DateOnly.FromDateTime(local);
            var next = clock.ToUtc(local.Hour < hour ? date : date.AddDays(1), new TimeOnly(hour, 0));
            var wait = next - clock.UtcNow;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            try
            {
                var requested = await curator.WaitForRequestAsync(wait, stoppingToken);
                await curator.TryRunAsync(full: !requested, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogError(e, "자동 정리 실패");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ContinueWith(_ => { });
            }
        }
    }
}
