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
/// 출퇴근 기록. 병원에 있는지 확인하는 방법은 두 가지입니다.
/// ① 병원 네트워크: 병원 와이파이의 공인 IP(설정에 등록)에서 누르면 통과
/// ② 입구 QR: 입구 화면(키오스크)에 30초마다 바뀌는 QR을 띄우고, 직원 폰으로 찍어서 열면 통과
/// 확인을 끄면 어디서든 누를 수 있고 "확인 없음"으로 남습니다.
/// </summary>
public static class AttendanceEndpoints
{
    /// <summary>자정을 넘겨 퇴근하는 경우, 이 시간 안의 전날 출근 기록에 퇴근을 붙입니다.</summary>
    private static readonly TimeSpan OvernightWindow = TimeSpan.FromHours(16);

    public static void MapAttendanceEndpoints(this RouteGroupBuilder intranet)
    {
        var g = intranet.MapGroup("/attendance");
        g.MapGet("/me", Mine);
        g.MapPost("/check-in", CheckIn);
        g.MapPost("/check-out", CheckOut);
        g.MapGet("/daily", Daily).RequireAuthorization(Policies.ManageStaff);
        g.MapGet("/staff/{staffId:int}", OfStaff).RequireAuthorization(Policies.ManageStaff);
        g.MapPut("/staff/{staffId:int}/{date}", Edit).RequireAuthorization(Policies.ManageStaff);
        g.MapGet("/kiosk", Kiosk).RequireAuthorization(Policies.ManageStaff);
    }

    /* ---------- 내 출퇴근 ---------- */

    private static async Task<IResult> Mine(string? month, ClaimsPrincipal user, HttpContext http, AppDbContext db, ClinicClock clock, CancellationToken ct) =>
        await MonthAsync(AuthEndpoints.StaffId(user), month, http, db, clock, ct);

    private static async Task<IResult> OfStaff(int staffId, string? month, HttpContext http, AppDbContext db, ClinicClock clock, CancellationToken ct) =>
        await db.Staff.AnyAsync(s => s.Id == staffId, ct) ? await MonthAsync(staffId, month, http, db, clock, ct) : Problems.NotFound("직원을 찾을 수 없습니다.");

    private static async Task<IResult> MonthAsync(int staffId, string? month, HttpContext http, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var today = clock.Today;
        var first = new DateOnly(today.Year, today.Month, 1);
        if (month is not null && !DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out first))
            return Problems.Validation("month", "달은 2026-11처럼 보내 주세요.");
        var last = first.AddMonths(1).AddDays(-1);
        var from = first < today ? first : today;
        var to = last > today ? last : today;

        var settings = await ClinicSetup.SettingsAsync(db, ct);
        var cal = await ClinicSetup.CalendarAsync(db, from, to, ct);
        var records = await db.Attendance.AsNoTracking().Where(a => a.StaffId == staffId && a.Date >= from && a.Date <= to).ToDictionaryAsync(a => a.Date, ct);
        var leaves = await ApprovedLeavesAsync(db, [staffId], from, to, ct);
        var since = Since(await db.Staff.AsNoTracking().FirstAsync(s => s.Id == staffId, ct), clock);

        AttendanceDayDto Day(DateOnly d) => BuildDay(d, records.GetValueOrDefault(d), LeaveOn(leaves, staffId, d), cal, settings, today, clock, since);
        var ip = ClinicSetup.ClientIp(http);
        var verify = new AttendanceVerifyDto(settings.AttendanceVerify, ClinicSetup.IsClinicNetwork(settings, ip), ip?.ToString());
        return Results.Ok(new MyAttendanceDto(
            first.ToString("yyyy-MM", CultureInfo.InvariantCulture), settings.WorkStart, settings.WorkEnd, verify, Day(today),
            WorkCalendar.Dates(first, last).Select(Day).ToList()));
    }

    /* ---------- 출근 · 퇴근 ---------- */

    private static async Task<IResult> CheckIn(CheckRequest? req, ClaimsPrincipal user, HttpContext http, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var settings = await ClinicSetup.SettingsAsync(db, ct);
        if (Verify(settings, req?.Code, http, clock) is not { } method)
            return Problems.Forbidden("병원에서만 출근을 기록할 수 있습니다. 병원 와이파이에 연결하거나 입구 화면의 QR을 찍어 주세요.", "not-at-clinic");

        var me = AuthEndpoints.StaffId(user);
        var today = clock.Today;
        var rec = await db.Attendance.FirstOrDefaultAsync(a => a.StaffId == me && a.Date == today, ct);
        if (rec?.CheckInAt is { } at) return Problems.Conflict($"이미 {clock.ToLocal(at):HH:mm}에 출근했습니다.");
        if (rec is null)
        {
            rec = new AttendanceRecord { StaffId = me, Date = today };
            db.Attendance.Add(rec);
        }
        rec.CheckInAt = clock.UtcNow;
        rec.CheckInMethod = method;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Problems.Conflict("이미 출근했습니다."); // 두 번 빠르게 누른 경우
        }
        return Results.Ok(await DayDtoAsync(db, rec, clock, settings, ct));
    }

    /// <summary>퇴근은 다시 눌러도 마지막 시각으로 바뀝니다(퇴근 후 다시 일한 경우).</summary>
    private static async Task<IResult> CheckOut(CheckRequest? req, ClaimsPrincipal user, HttpContext http, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var settings = await ClinicSetup.SettingsAsync(db, ct);
        if (Verify(settings, req?.Code, http, clock) is not { } method)
            return Problems.Forbidden("병원에서만 퇴근을 기록할 수 있습니다. 병원 와이파이에 연결하거나 입구 화면의 QR을 찍어 주세요.", "not-at-clinic");

        var me = AuthEndpoints.StaffId(user);
        var today = clock.Today;
        var now = clock.UtcNow;
        var rec = await db.Attendance.FirstOrDefaultAsync(a => a.StaffId == me && a.Date == today && a.CheckInAt != null, ct)
                  ?? await db.Attendance.FirstOrDefaultAsync(a => a.StaffId == me && a.Date == today.AddDays(-1) && a.CheckInAt != null && a.CheckOutAt == null, ct);
        if (rec is null || (rec.Date != today && now - rec.CheckInAt!.Value > OvernightWindow))
            return Problems.Conflict("오늘 출근 기록이 없습니다. 출근을 먼저 누르거나, 대표원장·관리자에게 수정을 요청해 주세요.");

        rec.CheckOutAt = now;
        rec.CheckOutMethod = method;
        await db.SaveChangesAsync(ct);
        return Results.Ok(await DayDtoAsync(db, rec, clock, settings, ct));
    }

    /// <summary>병원에 있는지: 병원 네트워크 → 입구 QR → (확인을 껐다면) 확인 없음. 통과 못 하면 null.</summary>
    private static AttendanceMethod? Verify(ClinicSettings s, string? code, HttpContext http, ClinicClock clock)
    {
        if (ClinicSetup.IsClinicNetwork(s, ClinicSetup.ClientIp(http))) return AttendanceMethod.Network;
        if (AttendanceCodes.IsValid(s.AttendanceSecret, code, clock.UtcNow)) return AttendanceMethod.Qr;
        return s.AttendanceVerify ? null : AttendanceMethod.Unverified;
    }

    /* ---------- 관리 ---------- */

    /// <summary>하루 현황: 사용 중인 모든 직원의 출퇴근·휴가</summary>
    private static async Task<IResult> Daily(DateOnly? date, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var today = clock.Today;
        var d = date ?? today;
        var settings = await ClinicSetup.SettingsAsync(db, ct);
        var cal = await ClinicSetup.CalendarAsync(db, d, d, ct);
        var staff = await db.Staff.AsNoTracking().Where(s => s.Status == StaffStatus.Active).OrderBy(s => s.Name).ToListAsync(ct);
        var ids = staff.Select(s => s.Id).ToList();
        var records = await db.Attendance.AsNoTracking().Where(a => a.Date == d && ids.Contains(a.StaffId)).ToDictionaryAsync(a => a.StaffId, ct);
        var leaves = await ApprovedLeavesAsync(db, ids, d, d, ct);
        var rows = staff.Select(s => new DailyAttendanceRowDto(s.Id, s.Name, s.Role, s.Department,
            BuildDay(d, records.GetValueOrDefault(s.Id), LeaveOn(leaves, s.Id, d), cal, settings, today, clock, Since(s, clock)))).ToList();
        return Results.Ok(new DailyAttendanceDto(d, cal.IsWorkday(d), cal.HolidayName(d), rows));
    }

    /// <summary>관리자 수정: 시각은 병원 현지 HH:mm. 둘 다 비우면 그날 기록을 지웁니다. 사유는 필수.</summary>
    private static async Task<IResult> Edit(int staffId, string date, AttendanceEditRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return Problems.Validation("date", "날짜 형식이 올바르지 않습니다.");
        if (d > clock.Today) return Problems.Validation("date", "앞날의 출퇴근은 기록할 수 없습니다.");
        if (!await db.Staff.AnyAsync(s => s.Id == staffId, ct)) return Problems.NotFound("직원을 찾을 수 없습니다.");
        var reason = StaffInput.Optional(req.Reason, 200);
        if (reason is null) return Problems.Validation("reason", "수정 사유를 적어 주세요. (예: 출근 버튼 누락)");
        if (!TryTime(req.CheckIn, out var inAt) || !TryTime(req.CheckOut, out var outAt)) return Problems.Validation("checkIn", "시각은 09:00처럼 적어 주세요.");
        if (inAt is null && outAt is not null) return Problems.Validation("checkIn", "퇴근만 있고 출근이 없을 수는 없습니다.");
        if (inAt is not null && outAt is not null && outAt <= inAt) return Problems.Validation("checkOut", "퇴근 시각은 출근 시각보다 늦어야 합니다.");

        var rec = await db.Attendance.FirstOrDefaultAsync(a => a.StaffId == staffId && a.Date == d, ct);
        if (inAt is null)
        {
            if (rec is not null) db.Attendance.Remove(rec);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }

        if (rec is null)
        {
            rec = new AttendanceRecord { StaffId = staffId, Date = d };
            db.Attendance.Add(rec);
        }
        var newIn = clock.ToUtc(d, inAt.Value);
        if (rec.CheckInAt != newIn) (rec.CheckInAt, rec.CheckInMethod) = (newIn, AttendanceMethod.Manual);
        var newOut = outAt is { } o ? clock.ToUtc(d, o) : (DateTime?)null;
        if (rec.CheckOutAt != newOut) (rec.CheckOutAt, rec.CheckOutMethod) = (newOut, newOut is null ? null : AttendanceMethod.Manual);
        rec.EditedAt = clock.UtcNow;
        rec.EditedByName = user.Identity?.Name;
        rec.EditReason = reason;
        await db.SaveChangesAsync(ct);
        return Results.Ok(await DayDtoAsync(db, rec, clock, await ClinicSetup.SettingsAsync(db, ct), ct));
    }

    /// <summary>입구 화면(키오스크)에 띄울 지금의 출근 QR</summary>
    private static async Task<IResult> Kiosk(HttpRequest http, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var s = await ClinicSetup.SettingsAsync(db, ct);
        var (code, expires) = AttendanceCodes.Current(s.AttendanceSecret, clock.UtcNow);
        var url = $"{http.Scheme}://{http.Host}/intranet/attendance?code={code}";
        return Results.Ok(new KioskCodeDto(code, url, expires, AttendanceCodes.StepSeconds));
    }

    /* ---------- 하루 상태 만들기 ---------- */

    private static bool TryTime(string? raw, out TimeOnly? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (!TimeOnly.TryParseExact(raw.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return false;
        value = t;
        return true;
    }

    public static async Task<List<LeaveRequest>> ApprovedLeavesAsync(AppDbContext db, List<int> staffIds, DateOnly from, DateOnly to, CancellationToken ct) =>
        await db.LeaveRequests.AsNoTracking()
            .Where(r => staffIds.Contains(r.StaffId) && r.Status == LeaveStatus.Approved && r.StartDate <= to && r.EndDate >= from)
            .ToListAsync(ct);

    /// <summary>그날의 승인된 휴가. 오전·오후 반차가 둘 다 있으면 종일로 봅니다.</summary>
    public static (LeaveType Type, LeavePortion Portion)? LeaveOn(List<LeaveRequest> leaves, int staffId, DateOnly d)
    {
        var mine = leaves.Where(r => r.StaffId == staffId && r.StartDate <= d && r.EndDate >= d).ToList();
        if (mine.Count == 0) return null;
        var full = mine.FirstOrDefault(r => r.Portion == LeavePortion.Full) ?? (mine.Count > 1 ? mine[0] : null);
        return full is not null ? (full.Type, LeavePortion.Full) : (mine[0].Type, mine[0].Portion);
    }

    /// <summary>결근을 따지기 시작하는 날: 계정을 만든 날과 입사일 중 늦은 날 (도입 전 날짜가 결근으로 보이지 않게)</summary>
    public static DateOnly Since(Staff s, ClinicClock clock)
    {
        var created = DateOnly.FromDateTime(clock.ToLocal(s.CreatedAt));
        return s.HireDate is { } h && h > created ? h : created;
    }

    public static AttendanceDayDto BuildDay(
        DateOnly d, AttendanceRecord? rec, (LeaveType Type, LeavePortion Portion)? leave, WorkCalendar cal, ClinicSettings s, DateOnly today, ClinicClock clock,
        DateOnly since = default)
    {
        var workday = cal.IsWorkday(d);
        var inLocal = rec?.CheckInAt is { } i ? TimeOnly.FromDateTime(clock.ToLocal(i)) : (TimeOnly?)null;
        var outLocal = rec?.CheckOutAt is { } o ? TimeOnly.FromDateTime(clock.ToLocal(o)) : (TimeOnly?)null;
        // 오전 반차면 지각, 오후 반차면 조퇴를 따지지 않습니다.
        // 분 단위로 봅니다: 09:00:40 출근은 지각이 아님.
        var late = workday && inLocal is { } a && a >= s.WorkStart.AddMinutes(1) && leave?.Portion is not (LeavePortion.AmHalf or LeavePortion.Full);
        var outDate = rec?.CheckOutAt is { } od ? DateOnly.FromDateTime(clock.ToLocal(od)) : (DateOnly?)null;
        var early = workday && outLocal is { } b && outDate == d && b < s.WorkEnd
                    && leave?.Portion is not (LeavePortion.PmHalf or LeavePortion.Full);

        var status = rec?.CheckInAt is not null ? (rec.CheckOutAt is null ? "Working" : "Done")
            : leave?.Portion == LeavePortion.Full ? "Leave"
            : !workday ? "Off"
            : d < today && d >= since ? "Absent"
            : "None";

        return new AttendanceDayDto(d, workday, cal.HolidayName(d), rec?.CheckInAt, rec?.CheckOutAt, rec?.CheckInMethod, rec?.CheckOutMethod,
            late, early, leave?.Type, leave?.Portion, status, rec?.EditReason, rec?.EditedByName);
    }

    private static async Task<AttendanceDayDto> DayDtoAsync(AppDbContext db, AttendanceRecord rec, ClinicClock clock, ClinicSettings s, CancellationToken ct)
    {
        var cal = await ClinicSetup.CalendarAsync(db, rec.Date, rec.Date, ct);
        var leaves = await ApprovedLeavesAsync(db, [rec.StaffId], rec.Date, rec.Date, ct);
        return BuildDay(rec.Date, rec, LeaveOn(leaves, rec.StaffId, rec.Date), cal, s, clock.Today, clock);
    }
}
