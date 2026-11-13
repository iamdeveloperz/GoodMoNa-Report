using GoodMoNa.Server.Contracts;
using GoodMoNa.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa.Server.Endpoints;

internal static class VisitQueries
{
    public static PhotoDto ToDto(int photoId, DateTime uploadedAt) => new(photoId, $"/api/photos/{photoId}", uploadedAt);

    public static async Task<VisitDto?> LoadAsync(AppDbContext db, int visitId, CancellationToken ct)
    {
        var v = await db.Visits.AsNoTracking()
            .Where(x => x.Id == visitId)
            .Select(x => new
            {
                x.Id,
                Patient = new PatientDto(x.Patient.Id, x.Patient.Name, x.Patient.BirthDate),
                x.PatientId,
                x.VisitDate,
                x.Status,
                x.Progress,
                x.Memo,
                x.ReportedAt,
                x.ReportedBy,
                Photo = x.Photos.OrderByDescending(p => p.Id).Select(p => new { p.Id, p.UploadedAt }).FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);
        if (v is null) return null;

        // 직전 사진: 이 방문보다 앞선 날짜 중 가장 최근 방문의 마지막 사진
        var prev = await db.Photos.AsNoTracking()
            .Where(p => p.Visit.PatientId == v.PatientId && p.Visit.VisitDate < v.VisitDate)
            .OrderByDescending(p => p.Visit.VisitDate).ThenByDescending(p => p.Id)
            .Select(p => new { p.Id, p.UploadedAt, p.Visit.VisitDate })
            .FirstOrDefaultAsync(ct);

        return new VisitDto(
            v.Id,
            v.Patient,
            v.VisitDate,
            v.Status,
            v.Progress,
            v.Memo,
            v.Photo is null ? null : ToDto(v.Photo.Id, v.Photo.UploadedAt),
            prev is null ? null : new PreviousPhotoDto(prev.VisitDate, v.VisitDate.DayNumber - prev.VisitDate.DayNumber, ToDto(prev.Id, prev.UploadedAt)),
            v.ReportedAt,
            v.ReportedBy);
    }
}
