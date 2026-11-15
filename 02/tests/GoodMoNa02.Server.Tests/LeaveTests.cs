using System.Net;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;

namespace GoodMoNa02.Server.Tests;

/// <summary>연차 신청 · 일수 계산 · 잔여 · 승인/반려/취소 · 조정 · 팀 달력</summary>
public sealed class LeaveTests : IDisposable
{
    private readonly TestApp _app = new();

    public LeaveTests() => _app.Time.SetKst(2026, 11, 16, 9); // 월요일

    public void Dispose() => _app.Dispose();

    private static object Annual(string start, string? end = null, string portion = "Full") =>
        new { type = "Annual", portion, startDate = start, endDate = end ?? start };

    [Fact]
    public async Task 주말과_공휴일은_빼고_세고_승인되면_잔여에서_빠진다()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse", hireDate: "2024-03-02"); // 근속 2년 → 15일

        var before = await nurse.Get<MyLeaveDto>("/api/intranet/leave/me");
        Assert.Equal(15m, before.Balance!.Granted);
        Assert.Equal(new DateOnly(2027, 3, 1), before.Balance.PeriodLastDay);

        // 12/24(목) ~ 12/28(월): 25일 성탄절·주말 제외 → 2일
        var r = await nurse.Post("/api/intranet/leave/requests", Annual("2026-12-24", "2026-12-28")).Ok<LeaveRequestDto>();
        Assert.Equal(2m, r.Days);
        Assert.Equal(LeaveStatus.Pending, r.Status);

        var pending = await nurse.Get<MyLeaveDto>("/api/intranet/leave/me");
        Assert.Equal(15m, pending.Balance!.Remaining);
        Assert.Equal(13m, pending.Balance.Available); // 승인 대기분은 "쓸 수 있는 연차"에서 미리 뺌

        await owner.Post($"/api/intranet/leave/requests/{r.Id}/approve").Ok<LeaveRequestDto>();
        var after = await nurse.Get<MyLeaveDto>("/api/intranet/leave/me");
        Assert.Equal(2m, after.Balance!.Used);
        Assert.Equal(13m, after.Balance.Remaining);
    }

    [Fact]
    public async Task 입사일이_없으면_연차는_못_쓰지만_병가는_신청할_수_있다()
    {
        var (staff, _) = await _app.MemberAsync("cs1", "Staff");
        var res = await staff.Post("/api/intranet/leave/requests", Annual("2026-11-20")).Status(HttpStatusCode.Conflict);
        Assert.Contains("입사일", await TestApp.Detail(res));

        await staff.Post("/api/intranet/leave/requests", new { type = "Sick", startDate = "2026-11-20" }).Status(HttpStatusCode.Created);
    }

    [Fact]
    public async Task 오전_오후_반차는_같은_날_함께_쓸_수_있지만_종일과는_겹칠_수_없다()
    {
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse", hireDate: "2024-03-02");
        var am = await nurse.Post("/api/intranet/leave/requests", Annual("2026-11-25", portion: "AmHalf")).Ok<LeaveRequestDto>();
        Assert.Equal(0.5m, am.Days);
        await nurse.Post("/api/intranet/leave/requests", Annual("2026-11-25", portion: "PmHalf")).Status(HttpStatusCode.Created);
        await nurse.Post("/api/intranet/leave/requests", Annual("2026-11-25", portion: "AmHalf")).Status(HttpStatusCode.Conflict);
        await nurse.Post("/api/intranet/leave/requests", Annual("2026-11-24", "2026-11-26")).Status(HttpStatusCode.Conflict);

        // 반차는 하루만, 근무일이 없는 날은 신청 불가
        await nurse.Post("/api/intranet/leave/requests", Annual("2026-11-26", "2026-11-27", "AmHalf")).Status(HttpStatusCode.BadRequest);
        await nurse.Post("/api/intranet/leave/requests", Annual("2026-11-21", "2026-11-22")).Status(HttpStatusCode.BadRequest); // 토·일
    }

    [Fact]
    public async Task 남은_연차보다_많이는_신청할_수_없다()
    {
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse", hireDate: "2024-03-02");
        // 12월 근무일 22일 (평일 23일 − 성탄절) > 15일
        var res = await nurse.Post("/api/intranet/leave/requests", Annual("2026-12-01", "2026-12-31")).Status(HttpStatusCode.Conflict);
        Assert.Contains("부족", await TestApp.Detail(res));
    }

    [Fact]
    public async Task 입사_1년_미만은_아직_생기지_않은_연차를_당겨_쓸_수_없다()
    {
        var (nurse, _) = await _app.MemberAsync("new1", "Nurse", hireDate: "2026-09-01"); // 11/16 기준 10/1, 11/1 → 2일
        var mine = await nurse.Get<MyLeaveDto>("/api/intranet/leave/me");
        Assert.Equal(2m, mine.Balance!.Granted);
        Assert.Equal(new DateOnly(2026, 12, 1), mine.Balance.NextGrantDate);

        await nurse.Post("/api/intranet/leave/requests", Annual("2026-11-18", "2026-11-20")).Status(HttpStatusCode.Conflict); // 3일 > 2일
        // 12/1에 1일이 더 생기므로 12/1~12/3(3일)은 가능
        await nurse.Post("/api/intranet/leave/requests", Annual("2026-12-01", "2026-12-03")).Status(HttpStatusCode.Created);
    }

    [Fact]
    public async Task 연차_기간을_넘는_신청은_나눠서_해야_한다()
    {
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse", hireDate: "2024-12-01");
        var res = await nurse.Post("/api/intranet/leave/requests", Annual("2026-11-30", "2026-12-01")).Status(HttpStatusCode.BadRequest);
        Assert.Contains("나눠서", await TestApp.Detail(res));
    }

    [Fact]
    public async Task 직원은_승인할_수_없고_관리자는_자기_신청을_승인할_수_없다()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, nurseId) = await _app.MemberAsync("nurse1", "Nurse", hireDate: "2024-03-02");
        var (admin, _) = await _app.MemberAsync("admin1", "Admin", hireDate: "2023-01-02");

        var r = await nurse.Post("/api/intranet/leave/requests", Annual("2026-11-27")).Ok<LeaveRequestDto>();
        await nurse.Post($"/api/intranet/leave/requests/{r.Id}/approve").Status(HttpStatusCode.Forbidden);
        await nurse.Get<LeaveCalendarDto>("/api/intranet/leave/calendar?month=2026-11"); // 달력은 누구나

        var mine = await admin.Post("/api/intranet/leave/requests", Annual("2026-11-30")).Ok<LeaveRequestDto>();
        await admin.Post($"/api/intranet/leave/requests/{mine.Id}/approve").Status(HttpStatusCode.Conflict);

        var pending = await owner.Get<List<PendingLeaveDto>>("/api/intranet/leave/requests");
        Assert.Equal(2, pending.Count);
        Assert.Equal(15m, pending.Single(p => p.Request.StaffId == nurseId).Balance!.Remaining);

        await owner.Post($"/api/intranet/leave/requests/{mine.Id}/reject", new { note = "인원 부족" }).Ok<LeaveRequestDto>();
        await admin.Post($"/api/intranet/leave/requests/{mine.Id}/cancel").Status(HttpStatusCode.Conflict); // 이미 반려
    }

    [Fact]
    public async Task 본인은_시작_전에만_승인된_휴가를_취소할_수_있다()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse", hireDate: "2024-03-02");
        var today = await nurse.Post("/api/intranet/leave/requests", Annual("2026-11-16")).Ok<LeaveRequestDto>();
        var later = await nurse.Post("/api/intranet/leave/requests", Annual("2026-11-30")).Ok<LeaveRequestDto>();
        await owner.Post($"/api/intranet/leave/requests/{today.Id}/approve").Ok<LeaveRequestDto>();
        await owner.Post($"/api/intranet/leave/requests/{later.Id}/approve").Ok<LeaveRequestDto>();

        await nurse.Post($"/api/intranet/leave/requests/{today.Id}/cancel").Status(HttpStatusCode.Conflict);
        var cancelled = await nurse.Post($"/api/intranet/leave/requests/{later.Id}/cancel").Ok<LeaveRequestDto>();
        Assert.Equal(LeaveStatus.Cancelled, cancelled.Status);
        await owner.Post($"/api/intranet/leave/requests/{today.Id}/cancel", new { note = "출근함" }).Ok<LeaveRequestDto>();

        Assert.Equal(0m, (await nurse.Get<MyLeaveDto>("/api/intranet/leave/me")).Balance!.Used);
    }

    [Fact]
    public async Task 조정과_근무요일_설정이_잔여와_일수에_반영된다()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, id) = await _app.MemberAsync("nurse1", "Nurse", hireDate: "2024-03-02");

        await owner.Post("/api/intranet/leave/adjustments", new { staffId = id, days = 2.5, reason = "전년도 이월" }).Status(HttpStatusCode.Created);
        await owner.Post("/api/intranet/leave/adjustments", new { staffId = id, days = 0.3, reason = "잘못된 단위" }).Status(HttpStatusCode.BadRequest);
        await owner.Post("/api/intranet/leave/adjustments", new { staffId = id, days = 1, reason = "" }).Status(HttpStatusCode.BadRequest);
        var b = (await nurse.Get<MyLeaveDto>("/api/intranet/leave/me")).Balance!;
        Assert.Equal(2.5m, b.Adjusted);
        Assert.Equal(17.5m, b.Remaining);

        // 토요일은 기본 근무일이 아님 → 토요일 근무로 바꾸면 신청 가능
        await nurse.Post("/api/intranet/leave/requests", Annual("2026-11-21")).Status(HttpStatusCode.BadRequest);
        await owner.Put("/api/intranet/settings", new { workDays = new[] { 1, 2, 3, 4, 5, 6 }, attendanceVerify = false }).Ok<SettingsDto>();
        var sat = await nurse.Post("/api/intranet/leave/requests", Annual("2026-11-21")).Ok<LeaveRequestDto>();
        Assert.Equal(1m, sat.Days);
    }

    [Fact]
    public async Task 팀_달력에는_그_달의_휴가와_공휴일이_나온다()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse", hireDate: "2024-03-02", name: "김간호");
        await nurse.Post("/api/intranet/leave/requests", Annual("2026-12-24", "2026-12-28")).Status(HttpStatusCode.Created);
        await owner.Post("/api/intranet/holidays", new { date = "2026-12-31", name = "병원 휴진" }).Status(HttpStatusCode.Created);

        var cal = await nurse.Get<LeaveCalendarDto>("/api/intranet/leave/calendar?month=2026-12");
        Assert.Equal("2026-12", cal.Month);
        Assert.Equal([1, 2, 3, 4, 5], cal.WorkDays);
        Assert.Contains(cal.Holidays, h => h.Name == "성탄절" && h.BuiltIn);
        Assert.Contains(cal.Holidays, h => h.Name == "병원 휴진" && !h.BuiltIn);
        var item = Assert.Single(cal.Items);
        Assert.Equal("김간호", item.Name);
        Assert.Equal(LeaveStatus.Pending, item.Status);
    }
}
