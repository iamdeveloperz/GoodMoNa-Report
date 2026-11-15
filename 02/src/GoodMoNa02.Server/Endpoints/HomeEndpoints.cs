using System.Security.Claims;
using GoodMoNa02.Server.Auth;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Infrastructure;
using GoodMoNa02.Server.Intranet;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa02.Server.Endpoints;

/// <summary>인트라넷 홈(대시보드) · 직원 주소록 · 내 연락처 수정</summary>
public static class HomeEndpoints
{
    public static void MapHomeEndpoints(this RouteGroupBuilder intranet)
    {
        intranet.MapGet("/home", Home);
        intranet.MapGet("/people", People);
        intranet.MapPut("/me/profile", UpdateProfile);
    }

    private static async Task<IResult> Home(ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var me = await db.Staff.AsNoTracking().FirstAsync(s => s.Id == AuthEndpoints.StaffId(user), ct);
        var today = clock.Today;
        var settings = await ClinicSetup.SettingsAsync(db, ct);
        var cal = await ClinicSetup.CalendarAsync(db, today, today, ct);

        var record = await db.Attendance.AsNoTracking().FirstOrDefaultAsync(a => a.StaffId == me.Id && a.Date == today, ct);
        var myLeaves = await AttendanceEndpoints.ApprovedLeavesAsync(db, [me.Id], today, today, ct);
        var attendance = AttendanceEndpoints.BuildDay(today, record, AttendanceEndpoints.LeaveOn(myLeaves, me.Id, today), cal, settings, today, clock, AttendanceEndpoints.Since(me, clock));

        var notices = await NoticeEndpoints.ListAsync(db, me.Id, null, ct);
        var offToday = await LeaveEndpoints.CalendarItemsAsync(db, today, today, ct, approvedOnly: true);

        ManageCountsDto? manage = null;
        if (LeaveEndpoints.IsManager(user))
        {
            manage = new ManageCountsDto(
                await db.Staff.CountAsync(s => s.Status == StaffStatus.Pending, ct),
                await db.LeaveRequests.CountAsync(r => r.Status == LeaveStatus.Pending, ct),
                await db.ParkingRequests.CountAsync(r => r.Status == ParkingRequestStatus.Pending, ct),
                await db.Staff.CountAsync(s => s.Status == StaffStatus.Active && s.HireDate == null, ct),
                await db.KbItems.CountAsync(i => i.Status == KbStatus.Draft, ct) + await db.AskLogs.CountAsync(a => a.FollowUp == AskFollowUp.Open, ct));
        }

        return Results.Ok(new HomeDto(
            today, cal.IsWorkday(today), cal.HolidayName(today), attendance,
            await LeaveLedger.BalanceAsync(db, me.Id, me.HireDate, today, today, today, ct),
            notices.Where(n => n.Required && !n.Read).ToList(),
            notices.Take(5).ToList(),
            offToday,
            manage));
    }

    /// <summary>주소록: 사용 중인 직원. 입사일은 대표원장·관리자에게만 보입니다.</summary>
    private static async Task<IResult> People(ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var manager = LeaveEndpoints.IsManager(user);
        var list = await db.Staff.AsNoTracking().Where(s => s.Status == StaffStatus.Active)
            .OrderBy(s => s.Department == null).ThenBy(s => s.Department).ThenBy(s => s.Name).ToListAsync(ct);
        return Results.Ok(list.Select(s => new PersonDto(s.Id, s.Name, s.Role, s.Department, s.Phone, manager ? s.HireDate : null)));
    }

    /// <summary>본인이 고칠 수 있는 것: 부서·연락처 (이름·직무·입사일은 관리자가)</summary>
    private static async Task<IResult> UpdateProfile(ProfileRequest req, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        if (StaffProfile.CheckPhone(req.Phone) is { } e) return Problems.Validation("phone", e);
        var me = await db.Staff.FirstAsync(s => s.Id == AuthEndpoints.StaffId(user), ct);
        me.Department = StaffInput.Optional(req.Department, 30);
        me.Phone = StaffInput.Optional(req.Phone, 20);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new PersonDto(me.Id, me.Name, me.Role, me.Department, me.Phone, null));
    }
}

public static class StaffProfile
{
    public static string? CheckPhone(string? phone) =>
        string.IsNullOrWhiteSpace(phone) || phone.Trim().All(c => char.IsAsciiDigit(c) || c is '-' or ' ' or '+') ? null : "연락처는 숫자와 -만 써 주세요.";
}
