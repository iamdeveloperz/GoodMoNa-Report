using GoodMoNa.Server.Contracts;
using GoodMoNa.Server.Data;
using GoodMoNa.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa.Server.Endpoints;

public static class PatientEndpoints
{
    public static void MapPatientEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/patients");
        g.MapGet("/", Search);
        g.MapPost("/", Register);
        g.MapPost("/{patientId:int}/visits/today", OpenTodayVisit);
    }

    /// <summary>이름 일부로 검색합니다. 동명이인은 생년월일 순으로 나란히 보여 줍니다.</summary>
    private static async Task<IResult> Search(string? q, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var term = PatientInput.NormalizeName(q);
        if (term.Length == 0) return Results.Ok(Array.Empty<PatientSummaryDto>());

        var today = clock.Today;
        var list = await db.Patients.AsNoTracking()
            .Where(p => p.Name.Contains(term))
            .OrderBy(p => p.Name).ThenBy(p => p.BirthDate)
            .Take(50)
            .Select(p => new PatientSummaryDto(
                p.Id,
                p.Name,
                p.BirthDate,
                p.Visits.Where(v => v.VisitDate < today).OrderByDescending(v => v.VisitDate).Select(v => (DateOnly?)v.VisitDate).FirstOrDefault(),
                p.Visits.Where(v => v.VisitDate == today).Select(v => (VisitStatus?)v.Status).FirstOrDefault()))
            .ToListAsync(ct);

        return Results.Ok(list);
    }

    private static async Task<IResult> Register(RegisterPatientRequest req, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var name = PatientInput.NormalizeName(req.Name);
        if (name.Length is 0 or > PatientInput.MaxNameLength)
            return Problems.Validation("name", $"이름을 1~{PatientInput.MaxNameLength}자로 입력해 주세요.");
        if (!PatientInput.TryParseBirth(req.Birth, clock.Today, out var birth))
            return Problems.Validation("birth", "생년월일을 YYYY-MM-DD 또는 YYMMDD로 입력해 주세요.");

        var existing = await db.Patients.AsNoTracking().FirstOrDefaultAsync(p => p.Name == name && p.BirthDate == birth, ct);
        if (existing is not null)
            return Problems.Conflict("이미 등록된 환자입니다.", new PatientDto(existing.Id, existing.Name, existing.BirthDate));

        var patient = new Patient { Name = name, BirthDate = birth, CreatedAt = clock.UtcNow };
        db.Patients.Add(patient);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/patients/{patient.Id}", new PatientSummaryDto(patient.Id, name, birth, null, null));
    }

    /// <summary>오늘 방문을 열거나(없으면 만들고) 돌려줍니다. 여러 번 눌러도 방문은 하나입니다.</summary>
    private static async Task<IResult> OpenTodayVisit(int patientId, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId, ct))
            return Problems.NotFound("환자를 찾을 수 없습니다.");

        var today = clock.Today;
        var visitId = await db.Visits.Where(v => v.PatientId == patientId && v.VisitDate == today).Select(v => (int?)v.Id).FirstOrDefaultAsync(ct);

        if (visitId is null)
        {
            var visit = new Visit { PatientId = patientId, VisitDate = today, Status = VisitStatus.Draft, CreatedAt = clock.UtcNow };
            db.Visits.Add(visit);
            try
            {
                await db.SaveChangesAsync(ct);
                visitId = visit.Id;
            }
            catch (DbUpdateException)
            {
                // 다른 PC에서 같은 순간에 만든 경우: 그쪽 방문을 씁니다.
                db.ChangeTracker.Clear();
                visitId = await db.Visits.Where(v => v.PatientId == patientId && v.VisitDate == today).Select(v => v.Id).FirstAsync(ct);
            }
        }

        return Results.Ok(await VisitQueries.LoadAsync(db, visitId.Value, ct));
    }
}
