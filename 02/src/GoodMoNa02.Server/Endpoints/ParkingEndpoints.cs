using System.Security.Claims;
using GoodMoNa02.Server.Auth;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Infrastructure;
using GoodMoNa02.Server.Intranet;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa02.Server.Endpoints;

/// <summary>
/// 직원 주차: 내 차량 등록 → 주차 배정 신청 → 대표원장·관리자가 주차면 배정(기간). 이중 주차 때는 번호 뒷자리로 차주를 찾아 연락합니다.
/// </summary>
public static class ParkingEndpoints
{
    public const int MaxVehiclesPerStaff = 3;

    public static void MapParkingEndpoints(this RouteGroupBuilder intranet)
    {
        var g = intranet.MapGroup("/parking");
        g.MapGet("/me", Mine);
        g.MapPost("/vehicles", AddVehicle);
        g.MapDelete("/vehicles/{id:int}", RemoveVehicle);
        g.MapPost("/requests", Apply);
        g.MapPost("/requests/{id:int}/cancel", CancelRequest);
        g.MapGet("/lookup", Lookup);

        g.MapGet("/spots", Spots).RequireAuthorization(Policies.ManageStaff);
        g.MapPost("/spots", AddSpot).RequireAuthorization(Policies.ManageStaff);
        g.MapPut("/spots/{id:int}", UpdateSpot).RequireAuthorization(Policies.ManageStaff);
        g.MapGet("/requests", Requests).RequireAuthorization(Policies.ManageStaff);
        g.MapPost("/requests/{id:int}/reject", RejectRequest).RequireAuthorization(Policies.ManageStaff);
        g.MapPost("/assignments", Assign).RequireAuthorization(Policies.ManageStaff);
        g.MapPost("/assignments/{id:int}/end", EndAssignment).RequireAuthorization(Policies.ManageStaff);
        g.MapGet("/vehicles", AllVehicles).RequireAuthorization(Policies.ManageStaff);
    }

    /* ---------- 내 차량 · 신청 ---------- */

    private static async Task<IResult> Mine(ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var me = AuthEndpoints.StaffId(user);
        var today = clock.Today;
        var vehicles = await db.Vehicles.AsNoTracking().Where(v => v.StaffId == me).OrderBy(v => v.Id).Select(v => new VehicleDto(v.Id, v.StaffId, v.Plate, v.Model)).ToListAsync(ct);
        var assignment = await AssignmentsQuery(db).Where(a => a.StaffId == me && (a.EndDate == null || a.EndDate >= today))
            .OrderBy(a => a.StartDate).FirstOrDefaultAsync(ct);
        var request = await RequestsQuery(db).Where(r => r.StaffId == me && r.Status == ParkingRequestStatus.Pending).FirstOrDefaultAsync(ct);
        return Results.Ok(new MyParkingDto(vehicles, assignment is null ? null : ToDto(assignment), request is null ? null : ToDto(request)));
    }

    private static async Task<IResult> AddVehicle(NewVehicleRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var plate = Plates.Normalize(req.Plate);
        if (!Plates.IsValid(plate)) return Problems.Validation("plate", "차량 번호를 12가3456 또는 123가4567 형식으로 적어 주세요.");
        var me = AuthEndpoints.StaffId(user);
        if (await db.Vehicles.CountAsync(v => v.StaffId == me, ct) >= MaxVehiclesPerStaff)
            return Problems.Conflict($"차량은 {MaxVehiclesPerStaff}대까지 등록할 수 있습니다.");
        if (await db.Vehicles.FirstOrDefaultAsync(v => v.Plate == plate, ct) is { } dup)
            return Problems.Conflict(dup.StaffId == me ? "이미 등록한 차량입니다." : "다른 직원의 차량으로 등록된 번호입니다. 관리자에게 확인해 주세요.");

        var v = new Vehicle { StaffId = me, Plate = plate, Model = StaffInput.Optional(req.Model, 40), CreatedAt = clock.UtcNow };
        db.Vehicles.Add(v);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Problems.Conflict("이미 등록된 번호입니다.");
        }
        return Results.Created($"/api/intranet/parking/vehicles/{v.Id}", new VehicleDto(v.Id, v.StaffId, v.Plate, v.Model));
    }

    /// <summary>본인 차량 또는 (관리자는) 누구의 차량이든. 배정에 연결돼 있었으면 배정은 남고 차량만 빠집니다.</summary>
    private static async Task<IResult> RemoveVehicle(int id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var v = await db.Vehicles.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (v is null) return Results.NoContent();
        if (v.StaffId != AuthEndpoints.StaffId(user) && !LeaveEndpoints.IsManager(user)) return Problems.Forbidden("다른 직원의 차량은 지울 수 없습니다.");
        db.Vehicles.Remove(v);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Apply(NewParkingRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var me = AuthEndpoints.StaffId(user);
        var today = clock.Today;
        if (await db.ParkingAssignments.AnyAsync(a => a.StaffId == me && (a.EndDate == null || a.EndDate >= today), ct))
            return Problems.Conflict("이미 배정된 주차면이 있습니다.");
        if (await db.ParkingRequests.AnyAsync(r => r.StaffId == me && r.Status == ParkingRequestStatus.Pending, ct))
            return Problems.Conflict("이미 신청해 두었습니다. 배정을 기다려 주세요.");
        if (req.VehicleId is { } vid && !await db.Vehicles.AnyAsync(v => v.Id == vid && v.StaffId == me, ct))
            return Problems.Validation("vehicleId", "내 차량을 골라 주세요.");
        if (req.VehicleId is null && !await db.Vehicles.AnyAsync(v => v.StaffId == me, ct))
            return Problems.Validation("vehicleId", "차량을 먼저 등록해 주세요.");

        var vehicleId = req.VehicleId ?? await db.Vehicles.Where(v => v.StaffId == me).OrderBy(v => v.Id).Select(v => (int?)v.Id).FirstAsync(ct);
        var r = new ParkingRequest { StaffId = me, VehicleId = vehicleId, Note = StaffInput.Optional(req.Note, 200), Status = ParkingRequestStatus.Pending, CreatedAt = clock.UtcNow };
        db.ParkingRequests.Add(r);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/intranet/parking/requests/{r.Id}", ToDto(await RequestsQuery(db).FirstAsync(x => x.Id == r.Id, ct)));
    }

    private static async Task<IResult> CancelRequest(int id, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var r = await db.ParkingRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return Problems.NotFound("신청을 찾을 수 없습니다.");
        if (r.StaffId != AuthEndpoints.StaffId(user) && !LeaveEndpoints.IsManager(user)) return Problems.Forbidden("다른 직원의 신청은 취소할 수 없습니다.");
        if (r.Status != ParkingRequestStatus.Pending) return Problems.Conflict("대기 중인 신청만 취소할 수 있습니다.");
        r.Status = ParkingRequestStatus.Cancelled;
        r.DecidedAt = clock.UtcNow;
        r.DecidedByName = user.Identity?.Name;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// 이중 주차 연락: 번호 일부(보통 뒤 4자리)로 차주를 찾습니다. 숫자 2자리 이상 필요.
    /// 직원 연락처는 사내 정보이므로 로그인한 직원에게만 보입니다.
    /// </summary>
    private static async Task<IResult> Lookup(string? q, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var digits = Plates.Digits(q);
        var text = Plates.Normalize(q);
        if (digits.Length < 2) return Problems.Validation("q", "번호를 2자리 이상 적어 주세요. (보통 뒤 4자리)");
        var today = clock.Today;
        var vehicles = await db.Vehicles.AsNoTracking().Include(v => v.Staff).Where(v => v.Staff.Status == StaffStatus.Active).ToListAsync(ct);
        var spots = await db.ParkingAssignments.AsNoTracking().Include(a => a.Spot)
            .Where(a => a.StartDate <= today && (a.EndDate == null || a.EndDate >= today))
            .ToDictionaryAsync(a => a.StaffId, a => a.Spot.Name, ct);
        var hits = vehicles
            .Where(v => v.Plate.Contains(text, StringComparison.Ordinal) || Plates.Digits(v.Plate).EndsWith(digits, StringComparison.Ordinal))
            .Take(20)
            .Select(v => new PlateLookupDto(v.Plate, v.Model, v.Staff.Name, v.Staff.Role, v.Staff.Department, v.Staff.Phone, spots.GetValueOrDefault(v.StaffId)))
            .ToList();
        return Results.Ok(hits);
    }

    /* ---------- 관리 ---------- */

    private static async Task<IResult> Spots(AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var today = clock.Today;
        var spots = await db.ParkingSpots.AsNoTracking().OrderByDescending(s => s.Active).ThenBy(s => s.Name).ToListAsync(ct);
        var live = await AssignmentsQuery(db).Where(a => a.EndDate == null || a.EndDate >= today).OrderBy(a => a.StartDate).ToListAsync(ct);
        return Results.Ok(spots.Select(s =>
        {
            var mine = live.Where(a => a.SpotId == s.Id).ToList();
            var current = mine.FirstOrDefault(a => a.StartDate <= today);
            var next = mine.FirstOrDefault(a => a.StartDate > today);
            return new SpotDto(s.Id, s.Name, s.Note, s.Active, current is null ? null : ToDto(current), next is null ? null : ToDto(next));
        }));
    }

    private static async Task<IResult> AddSpot(SpotRequest req, AppDbContext db, CancellationToken ct)
    {
        var name = StaffInput.Optional(req.Name, 20);
        if (name is null) return Problems.Validation("name", "주차면 이름을 적어 주세요. (예: B1-03)");
        if (await db.ParkingSpots.AnyAsync(s => s.Name == name, ct)) return Problems.Conflict("같은 이름의 주차면이 있습니다.");
        var s = new ParkingSpot { Name = name, Note = StaffInput.Optional(req.Note, 100), Active = req.Active ?? true };
        db.ParkingSpots.Add(s);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/intranet/parking/spots/{s.Id}", new SpotDto(s.Id, s.Name, s.Note, s.Active, null, null));
    }

    private static async Task<IResult> UpdateSpot(int id, SpotRequest req, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var s = await db.ParkingSpots.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Problems.NotFound("주차면을 찾을 수 없습니다.");
        var name = StaffInput.Optional(req.Name, 20) ?? s.Name;
        if (name != s.Name && await db.ParkingSpots.AnyAsync(x => x.Name == name, ct)) return Problems.Conflict("같은 이름의 주차면이 있습니다.");
        if (req.Active == false && s.Active && await db.ParkingAssignments.AnyAsync(a => a.SpotId == id && (a.EndDate == null || a.EndDate >= clock.Today), ct))
            return Problems.Conflict("배정 중인 주차면은 끌 수 없습니다. 배정을 먼저 끝내 주세요.");
        s.Name = name;
        s.Note = req.Note is null ? s.Note : StaffInput.Optional(req.Note, 100);
        s.Active = req.Active ?? s.Active;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new SpotDto(s.Id, s.Name, s.Note, s.Active, null, null));
    }

    private static async Task<IResult> Requests(string? status, AppDbContext db, CancellationToken ct)
    {
        var st = ParkingRequestStatus.Pending;
        if (status is not null && (!Enum.TryParse(status, true, out st) || !Enum.IsDefined(st))) return Problems.Validation("status", "상태 값이 올바르지 않습니다.");
        var q = RequestsQuery(db).Where(r => r.Status == st);
        q = st == ParkingRequestStatus.Pending ? q.OrderBy(r => r.CreatedAt) : q.OrderByDescending(r => r.DecidedAt).Take(100);
        return Results.Ok((await q.ToListAsync(ct)).Select(ToDto));
    }

    private static async Task<IResult> RejectRequest(int id, DecisionRequest? req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var r = await db.ParkingRequests.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return Problems.NotFound("신청을 찾을 수 없습니다.");
        if (r.Status != ParkingRequestStatus.Pending) return Problems.Conflict("대기 중인 신청만 반려할 수 있습니다.");
        r.Status = ParkingRequestStatus.Rejected;
        r.DecidedAt = clock.UtcNow;
        r.DecidedByName = user.Identity?.Name;
        r.DecisionNote = StaffInput.Optional(req?.Note, 200);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// 배정: 주차면 하나에 같은 기간 한 명, 직원 한 명에 같은 기간 주차면 하나. 끝 날짜가 없으면 계속.
    /// 그 직원의 대기 중인 신청은 "배정됨"으로 닫힙니다.
    /// </summary>
    private static async Task<IResult> Assign(AssignRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        if (req.SpotId is not { } spotId) return Problems.Validation("spotId", "주차면을 골라 주세요.");
        if (req.StaffId is not { } staffId) return Problems.Validation("staffId", "직원을 골라 주세요.");
        var start = req.StartDate ?? clock.Today;
        if (req.EndDate is { } e && e < start) return Problems.Validation("endDate", "끝 날짜가 시작일보다 앞설 수 없습니다.");
        var end = req.EndDate ?? DateOnly.MaxValue;

        var spot = await db.ParkingSpots.FirstOrDefaultAsync(s => s.Id == spotId, ct);
        if (spot is null) return Problems.NotFound("주차면을 찾을 수 없습니다.");
        if (!spot.Active) return Problems.Conflict("사용하지 않는 주차면입니다.");
        var staff = await db.Staff.FirstOrDefaultAsync(s => s.Id == staffId, ct);
        if (staff is not { Status: StaffStatus.Active }) return Problems.Validation("staffId", "사용 중인 직원만 배정할 수 있습니다.");
        if (req.VehicleId is { } vid && !await db.Vehicles.AnyAsync(v => v.Id == vid && v.StaffId == staffId, ct))
            return Problems.Validation("vehicleId", "그 직원의 차량이 아닙니다.");

        var overlapping = db.ParkingAssignments.Where(a => a.StartDate <= end && (a.EndDate == null || a.EndDate >= start));
        if (await overlapping.Include(a => a.Staff).FirstOrDefaultAsync(a => a.SpotId == spotId, ct) is { } taken)
            return Problems.Conflict($"{spot.Name}은(는) 그 기간에 {taken.Staff.Name}에게 배정되어 있습니다.");
        if (await overlapping.Include(a => a.Spot).FirstOrDefaultAsync(a => a.StaffId == staffId, ct) is { } has)
            return Problems.Conflict($"{staff.Name}은(는) 그 기간에 이미 {has.Spot.Name}을(를) 쓰고 있습니다.");

        var pending = await db.ParkingRequests.FirstOrDefaultAsync(r => r.StaffId == staffId && r.Status == ParkingRequestStatus.Pending, ct);
        var vehicleId = req.VehicleId ?? pending?.VehicleId ?? await db.Vehicles.Where(v => v.StaffId == staffId).OrderBy(v => v.Id).Select(v => (int?)v.Id).FirstOrDefaultAsync(ct);
        var a = new ParkingAssignment
        {
            SpotId = spotId, StaffId = staffId, VehicleId = vehicleId, StartDate = start, EndDate = req.EndDate,
            CreatedAt = clock.UtcNow, AssignedByName = user.Identity?.Name ?? "관리자",
        };
        db.ParkingAssignments.Add(a);
        if (pending is not null)
        {
            pending.Status = ParkingRequestStatus.Assigned;
            pending.DecidedAt = clock.UtcNow;
            pending.DecidedByName = user.Identity?.Name;
            pending.DecisionNote = spot.Name;
        }
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/intranet/parking/assignments/{a.Id}", ToDto(await AssignmentsQuery(db).FirstAsync(x => x.Id == a.Id, ct)));
    }

    /// <summary>배정 끝내기: 끝 날짜를 정합니다. 시작 전 배정을 시작일보다 앞서 끝내면 배정을 지웁니다.</summary>
    private static async Task<IResult> EndAssignment(int id, EndAssignmentRequest req, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var a = await db.ParkingAssignments.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return Problems.NotFound("배정을 찾을 수 없습니다.");
        var end = req.EndDate ?? clock.Today;
        if (end < a.StartDate) db.ParkingAssignments.Remove(a);
        else a.EndDate = end;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AllVehicles(AppDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Vehicles.AsNoTracking().Where(v => v.Staff.Status == StaffStatus.Active).OrderBy(v => v.Staff.Name)
            .Select(v => new VehicleDto(v.Id, v.StaffId, v.Plate, v.Model)).ToListAsync(ct));

    /* ---------- 도우미 ---------- */

    private static IQueryable<ParkingAssignment> AssignmentsQuery(AppDbContext db) =>
        db.ParkingAssignments.AsNoTracking().Include(a => a.Spot).Include(a => a.Staff).Include(a => a.Vehicle);

    private static IQueryable<ParkingRequest> RequestsQuery(AppDbContext db) =>
        db.ParkingRequests.AsNoTracking().Include(r => r.Staff).Include(r => r.Vehicle);

    private static ParkingAssignmentDto ToDto(ParkingAssignment a) =>
        new(a.Id, a.SpotId, a.Spot.Name, a.Spot.Note, a.StaffId, a.Staff.Name, a.Vehicle?.Plate, a.StartDate, a.EndDate, a.AssignedByName);

    private static ParkingRequestDto ToDto(ParkingRequest r) =>
        new(r.Id, r.StaffId, r.Staff.Name, r.Staff.Role, r.Staff.Department, r.VehicleId, r.Vehicle?.Plate, r.Note, r.Status, r.CreatedAt, r.DecidedAt, r.DecidedByName, r.DecisionNote);
}
