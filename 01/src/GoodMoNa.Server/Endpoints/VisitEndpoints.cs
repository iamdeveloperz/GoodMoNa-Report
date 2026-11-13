using System.Security.Claims;
using GoodMoNa.Server.Auth;
using GoodMoNa.Server.Contracts;
using GoodMoNa.Server.Data;
using GoodMoNa.Server.Hubs;
using GoodMoNa.Server.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoodMoNa.Server.Endpoints;

public static class VisitEndpoints
{
    public const int MaxMemoLength = 500;

    public static void MapVisitEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/visits");
        g.MapGet("/{visitId:int}", Get);
        g.MapPost("/{visitId:int}/capture-tokens", IssueCaptureToken);
        g.MapPut("/{visitId:int}/report", SubmitReport);
    }

    private static async Task<IResult> Get(int visitId, AppDbContext db, CancellationToken ct) =>
        await VisitQueries.LoadAsync(db, visitId, ct) is { } dto ? Results.Ok(dto) : Problems.NotFound("방문을 찾을 수 없습니다.");

    /// <summary>QR용 일회용 토큰을 만듭니다. 새로 만들면 같은 방문의 이전 QR은 바로 무효가 됩니다.</summary>
    private static async Task<IResult> IssueCaptureToken(
        int visitId, HttpRequest http, AppDbContext db, ClinicClock clock, IOptions<AppOptions> options, CancellationToken ct)
    {
        var visit = await db.Visits.FirstOrDefaultAsync(v => v.Id == visitId, ct);
        if (visit is null) return Problems.NotFound("방문을 찾을 수 없습니다.");
        if (visit.Status == VisitStatus.Reported) return Problems.Conflict("이미 보고된 방문입니다.");
        if (visit.VisitDate != clock.Today) return Problems.Conflict("오늘 방문만 촬영할 수 있습니다.");

        var now = clock.UtcNow;
        await db.CaptureTokens
            .Where(t => t.VisitId == visitId && t.RevokedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        var token = CaptureTokens.Create();
        var expiresAt = now.AddMinutes(options.Value.CaptureTokenMinutes);
        db.CaptureTokens.Add(new CaptureToken { TokenHash = CaptureTokens.Hash(token), VisitId = visitId, CreatedAt = now, ExpiresAt = expiresAt });
        await db.SaveChangesAsync(ct);

        var baseUrl = string.IsNullOrWhiteSpace(options.Value.PublicBaseUrl)
            ? $"{http.Scheme}://{http.Host}{http.PathBase}"
            : options.Value.PublicBaseUrl.TrimEnd('/');

        return Results.Ok(new CaptureTokenDto(token, $"{baseUrl}/capture/{token}", expiresAt));
    }

    private static async Task<IResult> SubmitReport(
        int visitId, SubmitReportRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, IHubContext<VisitHub> hub, CancellationToken ct)
    {
        if (req.Progress is not { } progress || !Enum.IsDefined(progress))
            return Problems.Validation("progress", "경과(호전·유지·악화)를 선택해 주세요.");
        var memo = (req.Memo ?? "").Trim();
        if (memo.Length > MaxMemoLength)
            return Problems.Validation("memo", $"메모는 {MaxMemoLength}자까지 쓸 수 있습니다.");

        var visit = await db.Visits.Include(v => v.Patient).FirstOrDefaultAsync(v => v.Id == visitId, ct);
        if (visit is null) return Problems.NotFound("방문을 찾을 수 없습니다.");
        if (visit.Status == VisitStatus.Reported) return Problems.Conflict("이미 보고된 방문입니다.");
        if (visit.VisitDate != clock.Today) return Problems.Conflict("오늘 방문만 보고할 수 있습니다.");
        if (!await db.Photos.AnyAsync(p => p.VisitId == visitId, ct)) return Problems.Conflict("오늘 사진을 먼저 촬영해 주세요.");

        var now = clock.UtcNow;
        visit.Status = VisitStatus.Reported;
        visit.Progress = progress;
        visit.Memo = memo;
        visit.ReportedAt = now;
        // 로그인한 직원(출입증)으로 보고자를 남깁니다.
        (visit.ReportedById, visit.ReportedBy) = StaffAuth.Who(user);
        await db.SaveChangesAsync(ct);

        // 보고가 끝나면 남은 QR은 더 이상 쓸 수 없습니다.
        await db.CaptureTokens
            .Where(t => t.VisitId == visitId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        await hub.Clients.Group(VisitHub.VisitGroup(visitId)).SendAsync(VisitHub.Events.VisitReported, visitId, ct);
        await hub.Clients.Group(VisitHub.DirectorGroup).SendAsync(VisitHub.Events.ReportSubmitted,
            new ReportSubmittedEvent(visitId, visit.Patient.Name, visit.Patient.BirthDate, progress, now), ct);

        return Results.Ok(await VisitQueries.LoadAsync(db, visitId, ct));
    }
}
