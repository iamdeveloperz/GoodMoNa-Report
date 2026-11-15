using System.Security.Claims;
using GoodMoNa02.Server.Auth;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa02.Server.Endpoints;

/// <summary>공지사항: 대표원장·관리자가 쓰고, 모든 직원이 읽습니다. 필독 공지는 읽음 확인을 받습니다.</summary>
public static class NoticeEndpoints
{
    public static void MapNoticeEndpoints(this RouteGroupBuilder intranet)
    {
        var g = intranet.MapGroup("/notices");
        g.MapGet("/", List);
        g.MapGet("/{id:int}", Get);
        g.MapPost("/{id:int}/read", MarkRead);
        g.MapPost("/", Create).RequireAuthorization(Policies.ManageStaff);
        g.MapPut("/{id:int}", Update).RequireAuthorization(Policies.ManageStaff);
        g.MapDelete("/{id:int}", Delete).RequireAuthorization(Policies.ManageStaff);
        g.MapGet("/{id:int}/reads", Reads).RequireAuthorization(Policies.ManageStaff);
    }

    /// <summary>목록. 필독을 위로, 그다음 최신순. 읽음 수는 지금 사용 중인 직원 기준.</summary>
    public static async Task<List<NoticeListItemDto>> ListAsync(AppDbContext db, int meId, int? take, CancellationToken ct)
    {
        var active = await db.Staff.Where(s => s.Status == StaffStatus.Active).Select(s => s.Id).ToListAsync(ct);
        var q = db.Notices.AsNoTracking().OrderByDescending(n => n.Required).ThenByDescending(n => n.CreatedAt).AsQueryable();
        if (take is not null) q = q.Take(take.Value);
        var notices = await q.Select(n => new { n.Id, n.Title, n.Required, n.AuthorName, n.CreatedAt, n.UpdatedAt, Readers = n.Reads.Select(r => r.StaffId).ToList() }).ToListAsync(ct);
        return notices.Select(n => new NoticeListItemDto(
            n.Id, n.Title, n.Required, n.AuthorName, n.CreatedAt, n.UpdatedAt,
            n.Readers.Contains(meId), n.Readers.Count(active.Contains), active.Count)).ToList();
    }

    private static async Task<IResult> List(ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        Results.Ok(await ListAsync(db, AuthEndpoints.StaffId(user), null, ct));

    private static async Task<IResult> Get(int id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var me = AuthEndpoints.StaffId(user);
        var n = await db.Notices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (n is null) return Problems.NotFound("공지를 찾을 수 없습니다.");
        var readAt = await db.NoticeReads.Where(r => r.NoticeId == id && r.StaffId == me).Select(r => (DateTime?)r.ReadAt).FirstOrDefaultAsync(ct);
        return Results.Ok(new NoticeDto(n.Id, n.Title, n.Body, n.Required, n.AuthorId, n.AuthorName, n.CreatedAt, n.UpdatedAt, readAt));
    }

    /// <summary>읽음 표시. 같은 요청을 여러 번 보내도 처음 읽은 시각이 남습니다.</summary>
    private static async Task<IResult> MarkRead(int id, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        if (!await db.Notices.AnyAsync(n => n.Id == id, ct)) return Problems.NotFound("공지를 찾을 수 없습니다.");
        var me = AuthEndpoints.StaffId(user);
        if (!await db.NoticeReads.AnyAsync(r => r.NoticeId == id && r.StaffId == me, ct))
        {
            db.NoticeReads.Add(new NoticeRead { NoticeId = id, StaffId = me, ReadAt = clock.UtcNow });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                /* 두 탭이 동시에 읽음 처리: 이미 있음 */
            }
        }
        return Results.NoContent();
    }

    private static (string Title, string Body, IResult? Error) Check(NoticeRequest req)
    {
        var title = (req.Title ?? "").Trim();
        var body = (req.Body ?? "").Trim();
        if (title.Length is 0 or > NoticeLimits.Title) return ("", "", Problems.Validation("title", $"제목을 1~{NoticeLimits.Title}자로 써 주세요."));
        if (body.Length is 0 or > NoticeLimits.Body) return ("", "", Problems.Validation("body", $"내용을 1~{NoticeLimits.Body}자로 써 주세요."));
        return (title, body, null);
    }

    private static async Task<IResult> Create(NoticeRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var (title, body, error) = Check(req);
        if (error is not null) return error;
        var me = AuthEndpoints.StaffId(user);
        var n = new Notice
        {
            Title = title, Body = body, Required = req.Required, AuthorId = me,
            AuthorName = user.Identity?.Name ?? "관리자", CreatedAt = clock.UtcNow,
        };
        n.Reads.Add(new NoticeRead { StaffId = me, ReadAt = clock.UtcNow }); // 쓴 사람은 읽은 것으로
        db.Notices.Add(n);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/intranet/notices/{n.Id}", new NoticeDto(n.Id, n.Title, n.Body, n.Required, n.AuthorId, n.AuthorName, n.CreatedAt, null, clock.UtcNow));
    }

    /// <summary>
    /// 수정. 필독 공지의 내용이 바뀌면 읽음 확인을 다시 받습니다(바뀐 내용을 모두 봐야 하므로). 제목만 고치거나 일반 공지면 그대로 둡니다.
    /// </summary>
    private static async Task<IResult> Update(int id, NoticeRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var (title, body, error) = Check(req);
        if (error is not null) return error;
        var n = await db.Notices.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (n is null) return Problems.NotFound("공지를 찾을 수 없습니다.");

        var resetReads = req.Required && (n.Body != body || !n.Required);
        n.Title = title;
        n.Body = body;
        n.Required = req.Required;
        n.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        if (resetReads)
        {
            var me = AuthEndpoints.StaffId(user);
            await db.NoticeReads.Where(r => r.NoticeId == id && r.StaffId != me).ExecuteDeleteAsync(ct);
        }
        return Results.Ok(new NoticeDto(n.Id, n.Title, n.Body, n.Required, n.AuthorId, n.AuthorName, n.CreatedAt, n.UpdatedAt, null));
    }

    private static async Task<IResult> Delete(int id, AppDbContext db, CancellationToken ct)
    {
        await db.Notices.Where(n => n.Id == id).ExecuteDeleteAsync(ct); // 읽음 기록은 cascade
        return Results.NoContent();
    }

    /// <summary>읽은 사람 / 아직 안 읽은 사람 (지금 사용 중인 직원 기준)</summary>
    private static async Task<IResult> Reads(int id, AppDbContext db, CancellationToken ct)
    {
        if (!await db.Notices.AnyAsync(n => n.Id == id, ct)) return Problems.NotFound("공지를 찾을 수 없습니다.");
        var reads = await db.NoticeReads.Where(r => r.NoticeId == id).ToDictionaryAsync(r => r.StaffId, r => r.ReadAt, ct);
        var staff = await db.Staff.AsNoTracking().Where(s => s.Status == StaffStatus.Active).OrderBy(s => s.Name).ToListAsync(ct);
        var rows = staff.Select(s => new NoticeReaderDto(s.Id, s.Name, s.Role, s.Department, reads.TryGetValue(s.Id, out var at) ? at : null)).ToList();
        return Results.Ok(new NoticeReadsDto(
            rows.Where(r => r.ReadAt is not null).OrderBy(r => r.ReadAt).ToList(),
            rows.Where(r => r.ReadAt is null).ToList()));
    }
}
