using GoodMoNa.Server.Contracts;
using GoodMoNa.Server.Data;
using GoodMoNa.Server.Hubs;
using GoodMoNa.Server.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoodMoNa.Server.Endpoints;

/// <summary>폰 촬영 페이지가 쓰는 API. 로그인 대신 QR 속 토큰이 권한입니다.</summary>
public static class CaptureEndpoints
{
    public static void MapCaptureEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/capture/{token}");
        g.MapGet("/", GetInfo);
        g.MapPost("/photo", UploadPhoto).DisableAntiforgery();
    }

    private static async Task<IResult> GetInfo(string token, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var found = await FindAsync(token, db, ct);
        if (found is null) return Problems.NotFound("유효하지 않은 촬영 링크입니다. PC에서 QR을 다시 띄워 주세요.");

        var (t, visit) = found.Value;
        return Results.Ok(new CaptureInfoDto(
            PatientInput.MaskName(visit.Patient.Name),
            visit.Patient.BirthDate,
            visit.VisitDate,
            t.ExpiresAt,
            StateOf(t, visit, clock.UtcNow)));
    }

    private static async Task<IResult> UploadPhoto(
        string token, IFormFile? photo, AppDbContext db, ClinicClock clock, MediaStorage storage,
        IOptions<AppOptions> options, IHubContext<VisitHub> hub, CancellationToken ct)
    {
        var found = await FindAsync(token, db, ct);
        if (found is null) return Problems.NotFound("유효하지 않은 촬영 링크입니다. PC에서 QR을 다시 띄워 주세요.");

        var (t, visit) = found.Value;
        switch (StateOf(t, visit, clock.UtcNow))
        {
            case CaptureState.Reported: return Problems.Conflict("이미 보고된 방문입니다.");
            case CaptureState.Expired: return Problems.Gone("촬영 시간이 지났습니다. PC에서 QR을 다시 띄워 주세요.");
        }

        if (photo is null || photo.Length == 0) return Problems.Validation("photo", "사진 파일이 없습니다.");
        if (photo.Length > options.Value.MaxPhotoBytes)
            return Problems.TooLarge($"사진은 {options.Value.MaxPhotoBytes / 1024 / 1024}MB까지 올릴 수 있습니다.");

        using var buffer = new MemoryStream((int)photo.Length);
        await photo.CopyToAsync(buffer, ct);
        if (!MediaStorage.LooksLikeJpeg(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)))
            return Problems.Unsupported("JPEG 사진만 올릴 수 있습니다.");

        var now = clock.UtcNow;
        var path = await storage.SaveAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), "photos", ".jpg", now, ct);
        var entity = new Photo { VisitId = visit.Id, StoragePath = path, ContentType = "image/jpeg", SizeBytes = buffer.Length, UploadedAt = now };
        db.Photos.Add(entity);
        await db.SaveChangesAsync(ct);

        var dto = VisitQueries.ToDto(entity.Id, now);
        await hub.Clients.Group(VisitHub.VisitGroup(visit.Id)).SendAsync(VisitHub.Events.PhotoArrived, visit.Id, dto, ct);
        return Results.Created(dto.Url, dto);
    }

    private static CaptureState StateOf(CaptureToken t, Visit visit, DateTime now) =>
        visit.Status == VisitStatus.Reported ? CaptureState.Reported
        : t.RevokedAt is not null || t.ExpiresAt <= now ? CaptureState.Expired
        : CaptureState.Ready;

    private static async Task<(CaptureToken Token, Visit Visit)?> FindAsync(string token, AppDbContext db, CancellationToken ct)
    {
        if (!CaptureTokens.IsWellFormed(token)) return null;
        var hash = CaptureTokens.Hash(token);
        var t = await db.CaptureTokens.AsNoTracking()
            .Include(x => x.Visit).ThenInclude(v => v.Patient)
            .FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
        return t is null ? null : (t, t.Visit);
    }
}
