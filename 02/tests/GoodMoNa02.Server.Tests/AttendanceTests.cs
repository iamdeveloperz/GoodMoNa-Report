using System.Net;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;

namespace GoodMoNa02.Server.Tests;

/// <summary>출퇴근: 병원 네트워크·입구 QR 확인, 지각·조퇴, 휴가·결근 표시, 관리자 수정</summary>
public sealed class AttendanceTests : IDisposable
{
    private readonly TestApp _app = new();

    public AttendanceTests() => _app.Time.SetKst(2026, 11, 16, 8, 0); // 월요일 08:00

    public void Dispose() => _app.Dispose();

    private static HttpRequestMessage From(string ip, string url, object? body = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, url) { Content = System.Net.Http.Json.JsonContent.Create(body ?? new { }) };
        m.Headers.Add("Cf-Connecting-IP", ip); // Cloudflare가 붙여 주는 원래 IP
        return m;
    }

    private async Task Relogin(HttpClient b, string loginId) =>
        (await b.Post("/api/auth/login", new { loginId, password = TestApp.Password })).EnsureSuccessStatusCode();

    [Fact]
    public async Task 확인을_끄면_어디서든_출퇴근하고_지각_조퇴를_표시한다()
    {
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse");
        _app.Time.SetKst(2026, 11, 16, 9, 5);
        await Relogin(nurse, "nurse1");

        var inDay = await nurse.Post("/api/intranet/attendance/check-in").Ok<AttendanceDayDto>();
        Assert.Equal("Working", inDay.Status);
        Assert.Equal(AttendanceMethod.Unverified, inDay.CheckInMethod);
        Assert.True(inDay.Late);
        await nurse.Post("/api/intranet/attendance/check-in").Status(HttpStatusCode.Conflict);

        _app.Time.SetKst(2026, 11, 16, 17, 30);
        await Relogin(nurse, "nurse1");
        var outDay = await nurse.Post("/api/intranet/attendance/check-out").Ok<AttendanceDayDto>();
        Assert.Equal("Done", outDay.Status);
        Assert.True(outDay.EarlyLeave);

        var month = await nurse.Get<MyAttendanceDto>("/api/intranet/attendance/me?month=2026-11");
        Assert.Equal(30, month.Days.Count);
        Assert.Equal("Done", month.Today.Status);
        Assert.Equal("None", month.Days.Single(d => d.Date == new DateOnly(2026, 11, 13)).Status); // 계정 만들기 전은 결근 아님
        Assert.Equal("Off", month.Days.Single(d => d.Date == new DateOnly(2026, 11, 15)).Status);  // 일요일
    }

    [Fact]
    public async Task 확인을_켜면_병원_네트워크나_입구_QR에서만_출근할_수_있다()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse");
        await owner.Put("/api/intranet/settings", new { attendanceVerify = true, attendanceNetworks = new[] { "203.0.113.0/24" } }).Ok<SettingsDto>();
        await owner.Put("/api/intranet/settings", new { attendanceVerify = true, attendanceNetworks = new[] { "병원" } }).Status(HttpStatusCode.BadRequest);

        var res = await nurse.Post("/api/intranet/attendance/check-in").Status(HttpStatusCode.Forbidden);
        Assert.Contains("병원", await TestApp.Detail(res));
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.SendAsync(From("198.51.100.9", "/api/intranet/attendance/check-in"))).StatusCode);

        var ok = await nurse.SendAsync(From("203.0.113.7", "/api/intranet/attendance/check-in"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var day = (await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<AttendanceDayDto>(ok.Content, TestApp.Json))!;
        Assert.Equal(AttendanceMethod.Network, day.CheckInMethod);
        Assert.False(day.Late);
    }

    [Fact]
    public async Task 입구_QR은_30초마다_바뀌고_1분이_지나면_쓸_수_없다()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse");
        var (doctor, _) = await _app.MemberAsync("doctor1", "Doctor");
        await owner.Put("/api/intranet/settings", new { attendanceVerify = true }).Ok<SettingsDto>();
        // 직원은 키오스크 코드를 못 봄
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.GetAsync("/api/intranet/attendance/kiosk")).StatusCode);

        var kiosk = await owner.Get<KioskCodeDto>("/api/intranet/attendance/kiosk");
        Assert.EndsWith($"/intranet/attendance?code={kiosk.Code}", kiosk.Url);
        var day = await nurse.Post("/api/intranet/attendance/check-in", new { code = kiosk.Code }).Ok<AttendanceDayDto>();
        Assert.Equal(AttendanceMethod.Qr, day.CheckInMethod);

        _app.Time.Advance(TimeSpan.FromSeconds(75));
        await doctor.Post("/api/intranet/attendance/check-in", new { code = kiosk.Code }).Status(HttpStatusCode.Forbidden);
        await doctor.Post("/api/intranet/attendance/check-in", new { code = "AAAAAAAAAAAA" }).Status(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 관리자는_사유를_남기고_기록을_고치며_하루_현황에_휴가와_결근이_보인다()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, nurseId) = await _app.MemberAsync("nurse1", "Nurse", hireDate: "2024-03-02");
        var (_, doctorId) = await _app.MemberAsync("doctor1", "Doctor");
        var leave = await nurse.Post("/api/intranet/leave/requests", new { type = "Annual", startDate = "2026-11-17" }).Ok<LeaveRequestDto>();
        await owner.Post($"/api/intranet/leave/requests/{leave.Id}/approve").Ok<LeaveRequestDto>();

        // 다음 날: 간호사는 휴가, 원장은 출근 기록이 없음
        _app.Time.SetKst(2026, 11, 18, 10, 0);
        await owner.Post("/api/auth/login", new { loginId = "owner", password = TestApp.Password });
        var daily = await owner.Get<DailyAttendanceDto>("/api/intranet/attendance/daily?date=2026-11-17");
        Assert.Equal("Leave", daily.Rows.Single(r => r.StaffId == nurseId).Day.Status);
        Assert.Equal("Absent", daily.Rows.Single(r => r.StaffId == doctorId).Day.Status);

        await owner.Put($"/api/intranet/attendance/staff/{doctorId}/2026-11-17", new { checkIn = "09:00", checkOut = "18:00" }).Status(HttpStatusCode.BadRequest);
        await owner.Put($"/api/intranet/attendance/staff/{doctorId}/2026-11-17", new { checkIn = "18:00", checkOut = "09:00", reason = "x" }).Status(HttpStatusCode.BadRequest);
        var fixedDay = await owner.Put($"/api/intranet/attendance/staff/{doctorId}/2026-11-17", new { checkIn = "08:55", checkOut = "18:02", reason = "출근 버튼 누락" }).Ok<AttendanceDayDto>();
        Assert.Equal("Done", fixedDay.Status);
        Assert.Equal(AttendanceMethod.Manual, fixedDay.CheckInMethod);
        Assert.Equal("출근 버튼 누락", fixedDay.EditReason);
        Assert.False(fixedDay.Late);
        Assert.Equal(new DateTime(2026, 11, 16, 23, 55, 0, DateTimeKind.Utc), fixedDay.CheckInAt); // 08:55 KST

        await owner.Put($"/api/intranet/attendance/staff/{doctorId}/2026-11-19", new { checkIn = "09:00", reason = "x" }).Status(HttpStatusCode.BadRequest); // 앞날
    }
}
