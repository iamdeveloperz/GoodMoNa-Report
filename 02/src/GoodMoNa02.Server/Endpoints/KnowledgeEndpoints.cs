using System.Globalization;
using System.Security.Claims;
using GoodMoNa02.Server.Auth;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Infrastructure;
using GoodMoNa02.Server.Knowledge;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa02.Server.Endpoints;

/// <summary>
/// CS 지식 자료: 가져오기(추출) → 초안 → 관리자 승인 → 검색 대상.
/// 직원은 승인된 자료를 보고 새 자료를 "제안"(초안)할 수 있고, 승인·반려·내림·가져오기는 대표원장·관리자만 합니다.
/// </summary>
public static class KnowledgeEndpoints
{
    public static void MapKnowledgeEndpoints(this RouteGroupBuilder intranet)
    {
        var g = intranet.MapGroup("/kb");
        g.MapGet("/items", List);
        g.MapGet("/items/{id:int}", Get);
        g.MapPost("/items", Create);
        g.MapPut("/items/{id:int}", Update).RequireAuthorization(Policies.ManageStaff);
        g.MapPost("/items/{id:int}/approve", Approve).RequireAuthorization(Policies.ManageStaff);
        g.MapPost("/items/{id:int}/reject", Reject).RequireAuthorization(Policies.ManageStaff);
        g.MapPost("/items/{id:int}/archive", Archive).RequireAuthorization(Policies.ManageStaff);
        g.MapDelete("/items/{id:int}", Delete).RequireAuthorization(Policies.ManageStaff);
        g.MapPost("/approve", ApproveMany).RequireAuthorization(Policies.ManageStaff);
        g.MapPost("/import/preview", Preview).RequireAuthorization(Policies.ManageStaff);
        g.MapPost("/import", Import).RequireAuthorization(Policies.ManageStaff);
        g.MapGet("/imports", Imports).RequireAuthorization(Policies.ManageStaff);
        g.MapGet("/questions", Questions).RequireAuthorization(Policies.ManageStaff);
        g.MapPost("/questions/{id:int}/dismiss", Dismiss).RequireAuthorization(Policies.ManageStaff);
        g.MapPost("/questions/{id:int}/attach", Attach).RequireAuthorization(Policies.ManageStaff);
        g.MapGet("/overview", Overview).RequireAuthorization(Policies.ManageStaff);
    }

    public static readonly string[] Origins = ["직접", "Claude", "Codex", "기타 AI"];

    public static KbItemDto ToDto(KbItem i, DateOnly today) => new(
        i.Id, i.Kind, i.Status, i.Title, i.Body, i.Category, i.Keywords, i.Source, i.SourceRef, i.ImportId, i.ReviewBy,
        IsStale(i.Status, i.ReviewBy, today), i.CreatedAt, i.CreatedByName, i.UpdatedAt, i.DecidedAt, i.DecidedByName, i.RejectReason);

    /// <summary>승인된 자료인데 재검토일이 지남</summary>
    public static bool IsStale(KbStatus status, DateOnly? reviewBy, DateOnly today) => status == KbStatus.Published && reviewBy < today;

    private static string Name(ClaimsPrincipal user) => user.Identity?.Name ?? "직원";

    /// <summary>
    /// 목록. 직원: 승인된 자료(mine=true면 내가 제안한 자료). 관리자: status로 거름(없으면 전부).
    /// q가 있으면 제목·본문·보조어에 들어 있는 것만.
    /// </summary>
    private static async Task<IResult> List(ClaimsPrincipal user, AppDbContext db, ClinicClock clock, KbStatus? status, bool? mine, string? q, CancellationToken ct)
    {
        var query = db.KbItems.AsNoTracking();
        var me = AuthEndpoints.StaffId(user);
        if (mine == true) query = query.Where(i => i.CreatedById == me && i.Source != KbSource.Import);
        else if (!LeaveEndpoints.IsManager(user)) query = query.Where(i => i.Status == KbStatus.Published);
        else if (status is not null) query = query.Where(i => i.Status == status);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            query = query.Where(i => i.Title.Contains(t) || i.Body.Contains(t) || i.Keywords.Contains(t));
        }
        var items = await query.OrderBy(i => i.Category).ThenBy(i => i.Kind).ThenBy(i => i.Id).Take(1000).ToListAsync(ct);
        return Results.Ok(items.Select(i => ToDto(i, clock.Today)).ToList());
    }

    private static async Task<IResult> Get(int id, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var i = await db.KbItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (i is null || !CanSee(user, i)) return Problems.NotFound("자료를 찾을 수 없습니다.");
        return Results.Ok(ToDto(i, clock.Today));
    }

    private static bool CanSee(ClaimsPrincipal user, KbItem i) =>
        i.Status == KbStatus.Published || LeaveEndpoints.IsManager(user) || i.CreatedById == AuthEndpoints.StaffId(user);

    private sealed record Checked(KbKind Kind, string Title, string Body, string Category, string Keywords, DateOnly? ReviewBy);

    private static (Checked? Value, IResult? Error) Check(KbItemRequest req, KbKind? fallbackKind = null)
    {
        var kind = req.Kind ?? fallbackKind ?? KbKind.Faq;
        var title = (req.Title ?? "").Trim();
        var body = (req.Body ?? "").Trim().Replace("\r\n", "\n");
        var label = kind == KbKind.Faq ? "질문" : "제목";
        if (title.Length is 0 or > KbLimits.Title) return (null, Problems.Validation("title", $"{label}을 1~{KbLimits.Title}자로 써 주세요."));
        if (body.Length is 0 or > KbLimits.Body) return (null, Problems.Validation("body", $"{(kind == KbKind.Faq ? "답" : "본문")}을 1~{KbLimits.Body}자로 써 주세요."));
        var category = (req.Category ?? "").Trim();
        if (category.Length > KbLimits.Category) return (null, Problems.Validation("category", $"분류는 {KbLimits.Category}자까지입니다."));
        var keywords = (req.Keywords ?? "").Trim();
        if (keywords.Length > KbLimits.Keywords) return (null, Problems.Validation("keywords", $"검색 보조어는 {KbLimits.Keywords}자까지입니다."));
        DateOnly? reviewBy = null;
        if (!string.IsNullOrWhiteSpace(req.ReviewBy))
        {
            if (!DateOnly.TryParseExact(req.ReviewBy, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return (null, Problems.Validation("reviewBy", "재검토일을 2026-12-31 형식으로 써 주세요."));
            reviewBy = d;
        }
        return (new Checked(kind, title, body, category.Length == 0 ? "일반" : category, keywords, reviewBy), null);
    }

    /// <summary>
    /// 새 자료. 직원이 쓰면 제안(초안), 관리자가 Publish=true로 쓰면 바로 승인.
    /// FromQuestionId: 못 찾은 질문으로 FAQ를 만들면 그 질문은 처리됨으로 바뀝니다(학습 고리).
    /// </summary>
    private static async Task<IResult> Create(KbItemRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, KbIndex index, CancellationToken ct)
    {
        var (v, error) = Check(req);
        if (error is not null) return error;
        var manager = LeaveEndpoints.IsManager(user);
        if ((req.Publish || req.FromQuestionId is not null) && !manager) return Problems.Forbidden("바로 올리기와 질문 처리는 대표원장·관리자만 할 수 있습니다.");

        AskLog? log = null;
        if (req.FromQuestionId is { } qid)
        {
            log = await db.AskLogs.FirstOrDefaultAsync(a => a.Id == qid, ct);
            if (log is null) return Problems.NotFound("질문 기록을 찾을 수 없습니다.");
        }

        var me = AuthEndpoints.StaffId(user);
        var item = new KbItem
        {
            Kind = v!.Kind, Status = KbStatus.Draft, Title = v.Title, Body = v.Body, Category = v.Category, Keywords = v.Keywords,
            ReviewBy = v.ReviewBy, Source = log is null ? KbSource.Manual : KbSource.Question, SourceRef = log is null ? null : $"질문 #{log.Id}",
            CreatedAt = clock.UtcNow, CreatedById = me, CreatedByName = Name(user),
        };
        if (req.Publish) Decide(item, KbStatus.Published, user, clock);
        db.KbItems.Add(item);
        await db.SaveChangesAsync(ct);
        if (log is not null)
        {
            log.FollowUp = AskFollowUp.Resolved;
            log.ResolvedItemId = item.Id;
            await db.SaveChangesAsync(ct);
        }
        if (item.Status == KbStatus.Published) index.Invalidate();
        return Results.Created($"/api/intranet/kb/items/{item.Id}", ToDto(item, clock.Today));
    }

    /// <summary>관리자 수정. 승인된 자료를 고치면 고친 관리자가 다시 승인한 것으로 기록합니다.</summary>
    private static async Task<IResult> Update(int id, KbItemRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, KbIndex index, CancellationToken ct)
    {
        var item = await db.KbItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return Problems.NotFound("자료를 찾을 수 없습니다.");
        var (v, error) = Check(req, item.Kind);
        if (error is not null) return error;
        (item.Kind, item.Title, item.Body, item.Category, item.Keywords, item.ReviewBy) = (v!.Kind, v.Title, v.Body, v.Category, v.Keywords, v.ReviewBy);
        item.UpdatedAt = clock.UtcNow;
        if (item.Status == KbStatus.Published || req.Publish) Decide(item, KbStatus.Published, user, clock);
        await db.SaveChangesAsync(ct);
        index.Invalidate();
        return Results.Ok(ToDto(item, clock.Today));
    }

    private static void Decide(KbItem item, KbStatus status, ClaimsPrincipal user, ClinicClock clock)
    {
        item.Status = status;
        item.DecidedAt = clock.UtcNow;
        item.DecidedById = AuthEndpoints.StaffId(user);
        item.DecidedByName = Name(user);
        if (status != KbStatus.Rejected) item.RejectReason = null;
    }

    /// <summary>승인: 초안·반려·내린 자료를 검색 대상으로</summary>
    private static async Task<IResult> Approve(int id, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, KbIndex index, CancellationToken ct)
    {
        var item = await db.KbItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return Problems.NotFound("자료를 찾을 수 없습니다.");
        if (item.Status == KbStatus.Published) return Problems.Conflict("이미 승인된 자료입니다.");
        Decide(item, KbStatus.Published, user, clock);
        await db.SaveChangesAsync(ct);
        index.Invalidate();
        return Results.Ok(ToDto(item, clock.Today));
    }

    /// <summary>여러 초안을 한 번에 승인 (가져오기로 만든 묶음을 검토한 뒤). 초안이 아닌 것은 건너뜀</summary>
    private static async Task<IResult> ApproveMany(KbIdsRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, KbIndex index, CancellationToken ct)
    {
        var ids = (req.Ids ?? []).Distinct().ToList();
        if (ids.Count == 0) return Problems.Validation("ids", "승인할 자료를 골라 주세요.");
        var items = await db.KbItems.Where(i => ids.Contains(i.Id) && i.Status == KbStatus.Draft).ToListAsync(ct);
        foreach (var i in items) Decide(i, KbStatus.Published, user, clock);
        await db.SaveChangesAsync(ct);
        index.Invalidate();
        return Results.Ok(new { approved = items.Count });
    }

    private static async Task<IResult> Reject(int id, KbRejectRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var reason = (req.Reason ?? "").Trim();
        if (reason.Length is 0 or > 200) return Problems.Validation("reason", "반려 사유를 1~200자로 써 주세요.");
        var item = await db.KbItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return Problems.NotFound("자료를 찾을 수 없습니다.");
        if (item.Status != KbStatus.Draft) return Problems.Conflict("초안만 반려할 수 있습니다. 승인된 자료는 \"내리기\"를 쓰세요.");
        Decide(item, KbStatus.Rejected, user, clock);
        item.RejectReason = reason;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(item, clock.Today));
    }

    /// <summary>내리기: 더 이상 맞지 않는 자료를 검색에서 뺌 (기록은 남음, 다시 승인 가능)</summary>
    private static async Task<IResult> Archive(int id, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, KbIndex index, CancellationToken ct)
    {
        var item = await db.KbItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return Problems.NotFound("자료를 찾을 수 없습니다.");
        if (item.Status != KbStatus.Published) return Problems.Conflict("승인된 자료만 내릴 수 있습니다.");
        Decide(item, KbStatus.Archived, user, clock);
        await db.SaveChangesAsync(ct);
        index.Invalidate();
        return Results.Ok(ToDto(item, clock.Today));
    }

    /// <summary>지우기: 승인된 자료는 먼저 내려야 함 (실수로 검색 중인 자료를 지우지 않게)</summary>
    private static async Task<IResult> Delete(int id, AppDbContext db, CancellationToken ct)
    {
        var item = await db.KbItems.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return Results.NoContent();
        if (item.Status == KbStatus.Published) return Problems.Conflict("승인된 자료는 먼저 내린 뒤 지울 수 있습니다.");
        await db.AskLogs.Where(a => a.ResolvedItemId == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ResolvedItemId, (int?)null), ct);
        db.KbItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static (string Text, string Name, string Origin, IResult? Error) CheckImport(KbImportRequest req)
    {
        var text = req.Text ?? "";
        if (text.Trim().Length == 0) return ("", "", "", Problems.Validation("text", "가져올 글을 붙여넣거나 파일을 골라 주세요."));
        if (text.Length > KbLimits.ImportText) return ("", "", "", Problems.Validation("text", $"한 번에 {KbLimits.ImportText:N0}자까지 가져올 수 있습니다. 나눠서 가져오세요."));
        var name = (req.Name ?? "").Trim();
        name = Path.GetFileNameWithoutExtension(name.Length == 0 ? "붙여넣기" : name);
        if (name.Length > 100) name = name[..100];
        var origin = Origins.FirstOrDefault(o => o == req.Origin) ?? "직접";
        return (text, name, origin, null);
    }

    /// <summary>미리 보기: 저장하지 않고 어떻게 잘릴지만 보여 줌</summary>
    private static IResult Preview(KbImportRequest req)
    {
        var (text, name, _, error) = CheckImport(req);
        return error ?? Results.Ok(KbExtractor.Extract(text, name, req.Category));
    }

    /// <summary>가져오기: 잘라 낸 자료를 모두 초안으로 저장 (승인 전에는 검색되지 않음)</summary>
    private static async Task<IResult> Import(KbImportRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var (text, name, origin, error) = CheckImport(req);
        if (error is not null) return error;
        var result = KbExtractor.Extract(text, name, req.Category);
        if (result.Items.Count == 0) return Problems.Validation("text", result.Warnings.FirstOrDefault() ?? "가져올 자료가 없습니다.");

        var me = AuthEndpoints.StaffId(user);
        var batch = new KbImport { Name = name, Origin = origin, ItemCount = result.Items.Count, CreatedAt = clock.UtcNow, CreatedByName = Name(user) };
        var items = result.Items.Select(d => new KbItem
        {
            Kind = d.Kind, Status = KbStatus.Draft, Title = d.Title, Body = d.Body, Category = d.Category, Keywords = d.Keywords,
            Source = KbSource.Import, SourceRef = name, Import = batch,
            CreatedAt = clock.UtcNow, CreatedById = me, CreatedByName = Name(user),
        }).ToList();
        db.KbItems.AddRange(items);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new KbImportResultDto(ToDto(batch), items.Select(i => ToDto(i, clock.Today)).ToList(), result.Warnings));
    }

    private static KbImportDto ToDto(KbImport b) => new(b.Id, b.Name, b.Origin, b.ItemCount, b.CreatedAt, b.CreatedByName);

    private static async Task<IResult> Imports(AppDbContext db, CancellationToken ct) =>
        Results.Ok((await db.KbImports.AsNoTracking().OrderByDescending(b => b.Id).Take(50).ToListAsync(ct)).Select(ToDto).ToList());

    /// <summary>질문 기록. 기본은 처리할 질문(Open: 못 찾음·부족함). followUp=All이면 최근 전부</summary>
    private static async Task<IResult> Questions(AppDbContext db, string? followUp, CancellationToken ct)
    {
        var q = db.AskLogs.AsNoTracking();
        if (!string.Equals(followUp, "All", StringComparison.OrdinalIgnoreCase))
        {
            var state = Enum.TryParse<AskFollowUp>(followUp, true, out var f) ? f : AskFollowUp.Open;
            q = q.Where(a => a.FollowUp == state);
        }
        var logs = await q.OrderByDescending(a => a.Id).Take(200).ToListAsync(ct);
        return Results.Ok(await ToDtosAsync(db, logs, ct));
    }

    public static async Task<List<AskLogDto>> ToDtosAsync(AppDbContext db, List<AskLog> logs, CancellationToken ct)
    {
        var ids = logs.Where(l => l.TopItemId is not null).Select(l => l.TopItemId!.Value).Distinct().ToList();
        var titles = await db.KbItems.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Title, ct);
        return logs.Select(l => new AskLogDto(
            l.Id, l.Question, l.StaffId, l.StaffName, l.CreatedAt, l.Found, l.Answered, l.Helpful, l.FollowUp,
            l.TopItemId, l.TopItemId is { } t ? titles.GetValueOrDefault(t) : null, l.TopCoverage, l.ResolvedItemId)).ToList();
    }

    /// <summary>답할 필요 없는 질문 닫기 (잡담·중복 등)</summary>
    private static async Task<IResult> Dismiss(int id, AppDbContext db, CancellationToken ct)
    {
        var n = await db.AskLogs.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.FollowUp, AskFollowUp.Dismissed), ct);
        return n == 0 ? Problems.NotFound("질문 기록을 찾을 수 없습니다.") : Results.NoContent();
    }

    /// <summary>
    /// 맞는 자료가 이미 있는데 고객 표현이 달라 못 찾은 질문: 그 질문을 자료의 검색 보조어로 붙입니다.
    /// 다음에 같은 표현으로 물으면 찾습니다(학습 고리의 가장 가벼운 형태).
    /// </summary>
    private static async Task<IResult> Attach(int id, KbAttachRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, KbIndex index, CancellationToken ct)
    {
        var log = await db.AskLogs.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (log is null) return Problems.NotFound("질문 기록을 찾을 수 없습니다.");
        var item = await db.KbItems.FirstOrDefaultAsync(i => i.Id == req.ItemId, ct);
        if (item is null || item.Status != KbStatus.Published) return Problems.Validation("itemId", "승인된 자료에만 표현을 붙일 수 있습니다.");
        var phrase = log.Question.Trim().TrimEnd('?', '？', '.', '!');
        if (phrase.Length > 60) return Problems.Validation("question", "질문이 길어 검색 보조어로 넣을 수 없습니다. FAQ로 만들어 주세요.");
        if (!item.Keywords.Contains(phrase))
        {
            var keywords = item.Keywords.Length == 0 ? phrase : $"{item.Keywords}, {phrase}";
            if (keywords.Length > KbLimits.Keywords) return Problems.Conflict("이 자료의 검색 보조어가 가득 찼습니다. 자료를 고쳐 정리하거나 FAQ로 만들어 주세요.");
            item.Keywords = keywords;
            item.UpdatedAt = clock.UtcNow;
            Decide(item, KbStatus.Published, user, clock);
        }
        log.FollowUp = AskFollowUp.Resolved;
        log.ResolvedItemId = item.Id;
        await db.SaveChangesAsync(ct);
        index.Invalidate();
        return Results.Ok(ToDto(item, clock.Today));
    }

    private static async Task<IResult> Overview(AppDbContext db, ClinicClock clock, IAnswerWriter writer, CancellationToken ct)
    {
        var today = clock.Today;
        var counts = await db.KbItems.GroupBy(i => i.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var stale = await db.KbItems.CountAsync(i => i.Status == KbStatus.Published && i.ReviewBy < today, ct);
        var open = await db.AskLogs.CountAsync(a => a.FollowUp == AskFollowUp.Open, ct);
        int Count(KbStatus s) => counts.FirstOrDefault(c => c.Key == s)?.Count ?? 0;
        return Results.Ok(new KbOverviewDto(Count(KbStatus.Draft), Count(KbStatus.Published), stale, open, writer.Status));
    }
}
