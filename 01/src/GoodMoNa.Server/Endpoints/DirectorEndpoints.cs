using GoodMoNa.Server.Contracts;
using GoodMoNa.Server.Data;
using GoodMoNa.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa.Server.Endpoints;

/// <summary>원장 앱(/director)이 쓰는 조회 API.</summary>
public static class DirectorEndpoints
{
    public static void MapDirectorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/director/reports", Reports);
        app.MapGet("/api/patients/{patientId:int}/history", History);
    }

    /// <summary>그날 올라온 보고 목록 (최근 보고가 위). date를 비우면 오늘.</summary>
    private static async Task<IResult> Reports(DateOnly? date, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var day = date ?? clock.Today;
        var list = await db.Visits.AsNoTracking()
            .Where(v => v.VisitDate == day && v.Status == VisitStatus.Reported)
            .OrderByDescending(v => v.ReportedAt)
            .Select(v => new ReportListItemDto(
                v.Id,
                v.PatientId,
                v.Patient.Name,
                v.Patient.BirthDate,
                v.Progress!.Value,
                v.ReportedAt!.Value,
                v.ReportedBy,
                v.VoiceNotes.Count,
                v.VoiceNotes.Count(n => n.Status == NoteStatus.Queued || n.Status == NoteStatus.Transcribing)))
            .ToListAsync(ct);
        return Results.Ok(list);
    }

    /// <summary>
    /// 환자 한 명의 기록을 방문 단위로 다시 묶어 돌려줍니다 (최근 방문이 위).
    /// 각 방문에 사진·경과·메모와, 그 방문에서 남긴 녹음·스크립트가 모두 들어 있습니다.
    /// </summary>
    private static async Task<IResult> History(int patientId, AppDbContext db, CancellationToken ct)
    {
        var patient = await db.Patients.AsNoTracking().FirstOrDefaultAsync(p => p.Id == patientId, ct);
        if (patient is null) return Problems.NotFound("환자를 찾을 수 없습니다.");

        var visits = await db.Visits.AsNoTracking()
            .Where(v => v.PatientId == patientId)
            .OrderByDescending(v => v.VisitDate)
            .Select(v => new
            {
                v.Id,
                v.VisitDate,
                v.Status,
                v.Progress,
                v.Memo,
                Photo = v.Photos.OrderByDescending(p => p.Id).Select(p => new { p.Id, p.UploadedAt }).FirstOrDefault()
            })
            .ToListAsync(ct);

        var visitIds = visits.Select(v => v.Id).ToList();
        var notes = await db.VoiceNotes.AsNoTracking().Include(n => n.Transcript)
            .Where(n => visitIds.Contains(n.VisitId))
            .OrderBy(n => n.Seq)
            .ToListAsync(ct);
        var byVisit = notes.ToLookup(n => n.VisitId);

        var dto = new PatientHistoryDto(
            new PatientDto(patient.Id, patient.Name, patient.BirthDate),
            visits.Select(v => new HistoryVisitDto(
                v.Id, v.VisitDate, v.Status, v.Progress, v.Memo,
                v.Photo is null ? null : VisitQueries.ToDto(v.Photo.Id, v.Photo.UploadedAt),
                byVisit[v.Id].Select(NoteQueries.ToDto).ToList())).ToList());

        return Results.Ok(dto);
    }
}
