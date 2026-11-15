using System.Globalization;
using System.Security.Claims;
using GoodMoNa02.Server.Auth;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Infrastructure;
using GoodMoNa02.Server.Intranet;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa02.Server.Endpoints;

/// <summary>
/// 연차·휴가: 신청 → 대표원장·관리자 승인/반려 → (필요하면) 취소. 잔여는 근로기준법 기준으로 자동 계산(<see cref="LeavePolicy"/>),
/// 일수는 근무 요일·공휴일·휴진일을 빼고 셉니다(<see cref="WorkCalendar"/>).
/// </summary>
public static class LeaveEndpoints
{
    /// <summary>지난 날짜로 신청할 수 있는 한도 (병가 사후 신청 등)</summary>
    public const int PastDaysAllowed = 30;
    public const int MaxSpanDays = 60;

    public static void MapLeaveEndpoints(this RouteGroupBuilder intranet)
    {
        var g = intranet.MapGroup("/leave");
        g.MapGet("/me", Mine);
        g.MapPost("/requests", Create);
        g.MapPost("/requests/{id:int}/cancel", Cancel);
        g.MapGet("/calendar", Calendar);

        g.MapGet("/requests", ListRequests).RequireAuthorization(Policies.ManageStaff);
        g.MapPost("/requests/{id:int}/approve", Approve).RequireAuthorization(Policies.ManageStaff);
        g.MapPost("/requests/{id:int}/reject", Reject).RequireAuthorization(Policies.ManageStaff);
        g.MapGet("/balances", Balances).RequireAuthorization(Policies.ManageStaff);
        g.MapGet("/adjustments", Adjustments).RequireAuthorization(Policies.ManageStaff);
        g.MapPost("/adjustments", AddAdjustment).RequireAuthorization(Policies.ManageStaff);
    }

    /* ---------- 내 연차 ---------- */

    private static async Task<IResult> Mine(ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var me = await db.Staff.AsNoTracking().FirstAsync(s => s.Id == AuthEndpoints.StaffId(user), ct);
        var today = clock.Today;
        var balance = await LeaveLedger.BalanceAsync(db, me.Id, me.HireDate, today, today, today, ct);
        var since = today.AddYears(-1);
        var requests = await db.LeaveRequests.AsNoTracking().Include(r => r.Staff)
            .Where(r => r.StaffId == me.Id && r.EndDate >= since).OrderByDescending(r => r.StartDate).ToListAsync(ct);
        var adjustments = await db.LeaveAdjustments.AsNoTracking().Where(a => a.StaffId == me.Id && a.EffectiveDate >= since)
            .OrderByDescending(a => a.EffectiveDate).ToListAsync(ct);
        return Results.Ok(new MyLeaveDto(me.HireDate, balance, await ToDtosAsync(db, requests, ct), adjustments.Select(ToDto).ToList()));
    }

    /* ---------- 신청 ---------- */

    private static async Task<IResult> Create(NewLeaveRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        if (!Enum.TryParse<LeaveType>(req.Type, true, out var type) || !Enum.IsDefined(type)) return Problems.Validation("type", "휴가 종류를 골라 주세요.");
        if (!Enum.TryParse<LeavePortion>(req.Portion ?? "Full", true, out var portion) || !Enum.IsDefined(portion)) return Problems.Validation("portion", "종일·반차를 골라 주세요.");
        if (req.StartDate is not { } start) return Problems.Validation("startDate", "시작일을 골라 주세요.");
        var end = req.EndDate ?? start;
        if (end < start) return Problems.Validation("endDate", "끝나는 날이 시작일보다 앞설 수 없습니다.");
        if (portion != LeavePortion.Full && end != start) return Problems.Validation("portion", "반차는 하루만 신청할 수 있습니다.");
        if (end.DayNumber - start.DayNumber >= MaxSpanDays) return Problems.Validation("endDate", $"한 번에 {MaxSpanDays}일까지 신청할 수 있습니다. 나눠서 신청해 주세요.");
        var today = clock.Today;
        if (start < today.AddDays(-PastDaysAllowed)) return Problems.Validation("startDate", $"지난 날짜는 {PastDaysAllowed}일 전까지만 신청할 수 있습니다.");
        var reason = StaffInput.Optional(req.Reason, 200);

        var me = await db.Staff.AsNoTracking().FirstAsync(s => s.Id == AuthEndpoints.StaffId(user), ct);
        var draft = new LeaveRequest { StaffId = me.Id, Type = type, Portion = portion, StartDate = start, EndDate = end, Reason = reason, Status = LeaveStatus.Pending, CreatedAt = clock.UtcNow };

        var cal = await ClinicSetup.CalendarAsync(db, start, end, ct);
        var days = cal.Days(draft);
        if (days == 0) return Problems.Validation("startDate", "고른 날짜에 근무일이 없습니다. (주말·공휴일·휴진일은 휴가로 세지 않습니다)");

        if (await FindOverlapAsync(db, draft, ct) is { } clash)
            return Problems.Conflict($"{Fmt(clash.StartDate)}{(clash.EndDate != clash.StartDate ? $"~{Fmt(clash.EndDate)}" : "")}에 이미 신청한 휴가가 있습니다.");

        if (type == LeaveType.Annual && await CheckAnnualAsync(db, me, draft, days, today, null, ct) is { } problem) return problem;

        db.LeaveRequests.Add(draft);
        await db.SaveChangesAsync(ct);
        draft.Staff = me;
        return Results.Created($"/api/intranet/leave/requests/{draft.Id}", ToDto(draft, days));
    }

    /// <summary>연차(차감) 신청 검사: 입사일, 연차 기간을 넘지 않는지, 남은 일수</summary>
    private static async Task<IResult?> CheckAnnualAsync(AppDbContext db, Staff staff, LeaveRequest r, decimal days, DateOnly today, int? excludeId, CancellationToken ct, bool countPending = true)
    {
        if (staff.HireDate is not { } hire) return Problems.Conflict("입사일이 등록되지 않아 연차를 계산할 수 없습니다. 대표원장·관리자에게 입사일 등록을 요청해 주세요.");
        if (r.StartDate < hire) return Problems.Validation("startDate", "입사일 전 날짜에는 연차를 쓸 수 없습니다.");
        var period = LeavePolicy.PeriodOf(hire, r.StartDate)!.Value;
        if (!period.Contains(r.EndDate))
            return Problems.Validation("endDate", $"연차 기간({Fmt(period.Start)}~{Fmt(period.LastDay)})을 넘습니다. {Fmt(period.End)} 전후로 나눠서 신청해 주세요.");

        // 1년 미만은 휴가 마지막 날까지 생기는 연차만 쓸 수 있습니다(미리 당겨 쓰기 없음).
        var asOf = r.EndDate > today ? r.EndDate : today;
        var b = (await LeaveLedger.BalanceAsync(db, staff.Id, hire, r.StartDate, asOf, today, ct, excludeId))!;
        var left = countPending ? b.Available : b.Remaining;
        if (days > left)
            return Problems.Conflict($"남은 연차가 부족합니다. (쓸 수 있는 연차 {Num(left)}일{(countPending && b.Pending > 0 ? $", 승인 대기 {Num(b.Pending)}일 제외" : "")} · 신청 {Num(days)}일)");
        return null;
    }

    /// <summary>같은 날 이미 있는 신청(대기·승인). 오전·오후 반차는 같은 날 함께 쓸 수 있습니다.</summary>
    private static Task<LeaveRequest?> FindOverlapAsync(AppDbContext db, LeaveRequest r, CancellationToken ct) =>
        db.LeaveRequests.AsNoTracking()
            .Where(x => x.StaffId == r.StaffId && x.Id != r.Id
                        && (x.Status == LeaveStatus.Pending || x.Status == LeaveStatus.Approved)
                        && x.StartDate <= r.EndDate && x.EndDate >= r.StartDate
                        && (x.Portion == LeavePortion.Full || r.Portion == LeavePortion.Full || x.Portion == r.Portion))
            .FirstOrDefaultAsync(ct);

    /// <summary>본인: 승인 대기 중이거나 아직 시작 안 한 승인 건. 대표원장·관리자: 언제든(사유를 남김).</summary>
    private static async Task<IResult> Cancel(int id, DecisionRequest? req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var r = await db.LeaveRequests.Include(x => x.Staff).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return Problems.NotFound("휴가 신청을 찾을 수 없습니다.");
        var me = AuthEndpoints.StaffId(user);
        var manager = IsManager(user);
        if (r.StaffId != me && !manager) return Problems.Forbidden("다른 직원의 휴가는 취소할 수 없습니다.");
        if (r.Status is not (LeaveStatus.Pending or LeaveStatus.Approved)) return Problems.Conflict("이미 반려되었거나 취소된 신청입니다.");
        if (!manager && r.Status == LeaveStatus.Approved && r.StartDate <= clock.Today)
            return Problems.Conflict("이미 시작된 휴가는 대표원장·관리자에게 취소를 요청해 주세요.");

        r.Status = LeaveStatus.Cancelled;
        Decide(r, user, clock, StaffInput.Optional(req?.Note, 200));
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ToDtoAsync(db, r, ct));
    }

    /* ---------- 승인 (대표원장·관리자) ---------- */

    private static async Task<IResult> ListRequests(string? status, ClinicClock clock, AppDbContext db, CancellationToken ct)
    {
        var st = LeaveStatus.Pending;
        if (status is not null && (!Enum.TryParse(status, true, out st) || !Enum.IsDefined(st))) return Problems.Validation("status", "상태 값이 올바르지 않습니다.");
        var q = db.LeaveRequests.AsNoTracking().Include(r => r.Staff).Where(r => r.Status == st);
        q = st == LeaveStatus.Pending ? q.OrderBy(r => r.StartDate) : q.OrderByDescending(r => r.DecidedAt).Take(100);
        var list = await q.ToListAsync(ct);
        var dtos = await ToDtosAsync(db, list, ct);
        var today = clock.Today;
        var rows = new List<PendingLeaveDto>();
        foreach (var (r, dto) in list.Zip(dtos))
        {
            var balance = r.Type == LeaveType.Annual
                ? await LeaveLedger.BalanceAsync(db, r.StaffId, r.Staff.HireDate, r.StartDate, r.EndDate > today ? r.EndDate : today, today, ct)
                : null;
            rows.Add(new PendingLeaveDto(dto, balance));
        }
        return Results.Ok(rows);
    }

    private static async Task<IResult> Approve(int id, DecisionRequest? req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var r = await db.LeaveRequests.Include(x => x.Staff).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return Problems.NotFound("휴가 신청을 찾을 수 없습니다.");
        if (r.Status != LeaveStatus.Pending) return Problems.Conflict("승인 대기 중인 신청만 승인할 수 있습니다.");
        if (r.StaffId == AuthEndpoints.StaffId(user) && !user.IsInRole(nameof(StaffRole.Owner)))
            return Problems.Conflict("자기 휴가는 승인할 수 없습니다. 다른 대표원장·관리자에게 요청해 주세요.");

        if (r.Type == LeaveType.Annual)
        {
            var cal = await ClinicSetup.CalendarAsync(db, r.StartDate, r.EndDate, ct);
            // 승인 시점에는 다른 대기 건과 상관없이, 이미 승인된 사용분 기준으로 남은 연차를 봅니다.
            if (await CheckAnnualAsync(db, r.Staff, r, cal.Days(r), clock.Today, r.Id, ct, countPending: false) is { } problem) return problem;
        }

        r.Status = LeaveStatus.Approved;
        Decide(r, user, clock, StaffInput.Optional(req?.Note, 200));
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ToDtoAsync(db, r, ct));
    }

    private static async Task<IResult> Reject(int id, DecisionRequest? req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var r = await db.LeaveRequests.Include(x => x.Staff).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return Problems.NotFound("휴가 신청을 찾을 수 없습니다.");
        if (r.Status != LeaveStatus.Pending) return Problems.Conflict("승인 대기 중인 신청만 반려할 수 있습니다.");
        r.Status = LeaveStatus.Rejected;
        Decide(r, user, clock, StaffInput.Optional(req?.Note, 200));
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ToDtoAsync(db, r, ct));
    }

    /* ---------- 팀 달력 ---------- */

    /// <summary>월 달력: 그 달에 걸친 휴가(대기·승인), 휴무일, 근무 요일. month = "2026-11", 비우면 이번 달.</summary>
    private static async Task<IResult> Calendar(string? month, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var first = new DateOnly(clock.Today.Year, clock.Today.Month, 1);
        if (month is not null && !DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out first))
            return Problems.Validation("month", "달은 2026-11처럼 보내 주세요.");
        var last = first.AddMonths(1).AddDays(-1);

        var settings = await ClinicSetup.SettingsAsync(db, ct);
        var holidays = await db.Holidays.AsNoTracking().Where(h => h.Date >= first && h.Date <= last).OrderBy(h => h.Date)
            .Select(h => new HolidayDto(h.Date, h.Name, h.BuiltIn)).ToListAsync(ct);
        var items = await CalendarItemsAsync(db, first, last, ct);
        return Results.Ok(new LeaveCalendarDto(first.ToString("yyyy-MM", CultureInfo.InvariantCulture), WorkCalendar.WeekdaysOf(settings.WorkDayMask), holidays, items, settings.LeaveWarnSameRole));
    }

    public static Task<List<CalendarLeaveDto>> CalendarItemsAsync(AppDbContext db, DateOnly from, DateOnly to, CancellationToken ct, bool approvedOnly = false) =>
        db.LeaveRequests.AsNoTracking()
            .Where(r => r.StartDate <= to && r.EndDate >= from
                        && (r.Status == LeaveStatus.Approved || (!approvedOnly && r.Status == LeaveStatus.Pending)))
            .OrderBy(r => r.StartDate).ThenBy(r => r.Staff.Name)
            .Select(r => new CalendarLeaveDto(r.Id, r.StaffId, r.Staff.Name, r.Staff.Role, r.Type, r.Portion, r.StartDate, r.EndDate, r.Status))
            .ToListAsync(ct);

    /* ---------- 잔여 현황 · 조정 (대표원장·관리자) ---------- */

    private static async Task<IResult> Balances(AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var today = clock.Today;
        var staff = await db.Staff.AsNoTracking().Where(s => s.Status == StaffStatus.Active).OrderBy(s => s.Name).ToListAsync(ct);
        var rows = new List<StaffBalanceDto>();
        foreach (var s in staff)
            rows.Add(new StaffBalanceDto(s.Id, s.Name, s.Role, s.Department, s.HireDate, await LeaveLedger.BalanceAsync(db, s.Id, s.HireDate, today, today, today, ct)));
        return Results.Ok(rows);
    }

    private static async Task<IResult> Adjustments(int staffId, AppDbContext db, CancellationToken ct) =>
        Results.Ok((await db.LeaveAdjustments.AsNoTracking().Where(a => a.StaffId == staffId).OrderByDescending(a => a.EffectiveDate).ToListAsync(ct)).Select(ToDto));

    /// <summary>이월·특별휴가(+), 도입 전 사용분(−) 등. 0.5일 단위, 사유 필수.</summary>
    private static async Task<IResult> AddAdjustment(NewAdjustmentRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        if (req.Days == 0 || Math.Abs(req.Days) > 30 || req.Days * 2 != Math.Truncate(req.Days * 2))
            return Problems.Validation("days", "조정 일수는 0.5일 단위로 −30~30일 사이에서 정해 주세요. (0 제외)");
        var reason = StaffInput.Optional(req.Reason, 200);
        if (reason is null) return Problems.Validation("reason", "조정 사유를 적어 주세요.");
        var staff = await db.Staff.AsNoTracking().FirstOrDefaultAsync(s => s.Id == req.StaffId, ct);
        if (staff is null) return Problems.NotFound("직원을 찾을 수 없습니다.");

        var a = new LeaveAdjustment
        {
            StaffId = staff.Id, Days = req.Days, EffectiveDate = req.EffectiveDate ?? clock.Today, Reason = reason,
            CreatedAt = clock.UtcNow, CreatedById = AuthEndpoints.StaffId(user), CreatedByName = user.Identity?.Name ?? "관리자",
        };
        db.LeaveAdjustments.Add(a);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/intranet/leave/adjustments?staffId={staff.Id}", ToDto(a));
    }

    /* ---------- 도우미 ---------- */

    public static bool IsManager(ClaimsPrincipal user) => Policies.ManageStaffRoles.Any(user.IsInRole);

    private static void Decide(LeaveRequest r, ClaimsPrincipal user, ClinicClock clock, string? note)
    {
        r.DecidedAt = clock.UtcNow;
        r.DecidedById = AuthEndpoints.StaffId(user);
        r.DecidedByName = user.Identity?.Name;
        r.DecisionNote = note;
    }

    private static string Fmt(DateOnly d) => d.ToString("M.d", CultureInfo.InvariantCulture);

    private static string Num(decimal d) => d.ToString("0.#", CultureInfo.InvariantCulture);

    private static LeaveRequestDto ToDto(LeaveRequest r, decimal days) => new(
        r.Id, r.StaffId, r.Staff.Name, r.Staff.Role, r.Staff.Department, r.Type, r.Portion, r.StartDate, r.EndDate, days,
        r.Reason, r.Status, r.CreatedAt, r.DecidedAt, r.DecidedByName, r.DecisionNote);

    private static async Task<LeaveRequestDto> ToDtoAsync(AppDbContext db, LeaveRequest r, CancellationToken ct) =>
        ToDto(r, (await ClinicSetup.CalendarAsync(db, r.StartDate, r.EndDate, ct)).Days(r));

    /// <summary>여러 신청의 일수를 달력 한 번으로 셉니다.</summary>
    private static async Task<List<LeaveRequestDto>> ToDtosAsync(AppDbContext db, List<LeaveRequest> list, CancellationToken ct)
    {
        if (list.Count == 0) return [];
        var cal = await ClinicSetup.CalendarAsync(db, list.Min(r => r.StartDate), list.Max(r => r.EndDate), ct);
        return list.Select(r => ToDto(r, cal.Days(r))).ToList();
    }

    private static LeaveAdjustmentDto ToDto(LeaveAdjustment a) => new(a.Id, a.StaffId, a.Days, a.EffectiveDate, a.Reason, a.CreatedAt, a.CreatedByName);
}
