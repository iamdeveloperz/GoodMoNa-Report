using System.Net;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;
using Microsoft.Extensions.DependencyInjection;

namespace GoodMoNa02.Server.Tests;

/// <summary>공지(필독·읽음 확인) · 주소록 · 홈 · 주차 · 화면 보호</summary>
public sealed class IntranetTests : IDisposable
{
    private readonly TestApp _app = new();

    public IntranetTests() => _app.Time.SetKst(2026, 11, 16, 9);

    public void Dispose() => _app.Dispose();

    /* ---------- 공지 ---------- */

    [Fact]
    public async Task 필독_공지는_읽을_때까지_홈에_남고_작성자는_안_읽은_사람을_본다()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, nurseId) = await _app.MemberAsync("nurse1", "Nurse", name: "김간호");
        var (_, doctorId) = await _app.MemberAsync("doctor1", "Doctor", name: "이원장");

        await nurse.Post("/api/intranet/notices", new { title = "x", body = "y" }).Status(HttpStatusCode.Forbidden);
        await owner.Post("/api/intranet/notices", new { title = "", body = "y" }).Status(HttpStatusCode.BadRequest);
        var n = await owner.Post("/api/intranet/notices", new { title = "감염관리 지침 변경", body = "12월부터 장갑 교체 기준이 바뀝니다.", required = true }).Ok<NoticeDto>();

        var home = await nurse.Get<HomeDto>("/api/intranet/home");
        Assert.Equal(n.Id, Assert.Single(home.RequiredUnread).Id);

        var detail = await nurse.Get<NoticeDto>($"/api/intranet/notices/{n.Id}");
        Assert.Null(detail.ReadAt);
        (await nurse.Post($"/api/intranet/notices/{n.Id}/read")).EnsureSuccessStatusCode();
        (await nurse.Post($"/api/intranet/notices/{n.Id}/read")).EnsureSuccessStatusCode(); // 두 번 눌러도 괜찮음
        Assert.Empty((await nurse.Get<HomeDto>("/api/intranet/home")).RequiredUnread);

        var reads = await owner.Get<NoticeReadsDto>($"/api/intranet/notices/{n.Id}/reads");
        Assert.Contains(reads.Read, r => r.StaffId == nurseId);
        Assert.Equal(doctorId, Assert.Single(reads.Unread).StaffId); // 작성자는 읽은 것으로
        var list = await owner.Get<List<NoticeListItemDto>>("/api/intranet/notices");
        Assert.Equal((2, 3), (list[0].ReadCount, list[0].TargetCount));

        // 필독 공지의 내용이 바뀌면 다시 확인받음 (제목만 바뀌면 그대로)
        await owner.Put($"/api/intranet/notices/{n.Id}", new { title = "감염관리 지침 변경(수정)", body = "12월부터 장갑 교체 기준이 바뀝니다.", required = true }).Ok<NoticeDto>();
        Assert.Empty((await nurse.Get<HomeDto>("/api/intranet/home")).RequiredUnread);
        await owner.Put($"/api/intranet/notices/{n.Id}", new { title = "감염관리 지침 변경", body = "1월부터로 연기되었습니다.", required = true }).Ok<NoticeDto>();
        Assert.Single((await nurse.Get<HomeDto>("/api/intranet/home")).RequiredUnread);

        (await owner.DeleteAsync($"/api/intranet/notices/{n.Id}")).EnsureSuccessStatusCode();
        Assert.Empty(await nurse.Get<List<NoticeListItemDto>>("/api/intranet/notices"));
    }

    /* ---------- 주소록 · 홈 ---------- */

    [Fact]
    public async Task 주소록의_입사일은_관리자에게만_보이고_연락처는_본인이_고친다()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, nurseId) = await _app.MemberAsync("nurse1", "Nurse", hireDate: "2024-03-02", phone: "010-1111-2222");

        var seenByNurse = await nurse.Get<List<PersonDto>>("/api/intranet/people");
        Assert.Null(seenByNurse.Single(p => p.Id == nurseId).HireDate);
        Assert.Equal("010-1111-2222", seenByNurse.Single(p => p.Id == nurseId).Phone);
        var seenByOwner = await owner.Get<List<PersonDto>>("/api/intranet/people");
        Assert.Equal(new DateOnly(2024, 3, 2), seenByOwner.Single(p => p.Id == nurseId).HireDate);

        await nurse.Put("/api/intranet/me/profile", new { department = "진료부", phone = "전화" }).Status(HttpStatusCode.BadRequest);
        var me = await nurse.Put("/api/intranet/me/profile", new { department = "상담실", phone = "010-3333-4444" }).Ok<PersonDto>();
        Assert.Equal("상담실", me.Department);

        // 관리자의 직원 정보 수정은 직원 관리 API (입사일 형식 검사)
        await owner.Put($"/api/admin/staff/{nurseId}/profile", new { hireDate = "2024-13-01" }).Status(HttpStatusCode.BadRequest);
        await nurse.Put($"/api/admin/staff/{nurseId}/profile", new { hireDate = "2024-01-01" }).Status(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 관리자_홈에는_처리할_일이_모인다()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse", hireDate: "2024-03-02");
        await _app.MemberAsync("cs1", "Staff"); // 입사일 없음
        await nurse.Post("/api/intranet/leave/requests", new { type = "Annual", startDate = "2026-11-20" }).Status(HttpStatusCode.Created);
        await nurse.Post("/api/intranet/parking/vehicles", new { plate = "12가3456" }).Status(HttpStatusCode.Created);
        await nurse.Post("/api/intranet/parking/requests", new { }).Status(HttpStatusCode.Created);
        await _app.Browser().Post("/api/auth/signup", new { loginId = "waiting", name = "대기", password = TestApp.Password, requestedRole = "Staff" });

        var home = await owner.Get<HomeDto>("/api/intranet/home");
        Assert.Equal(new ManageCountsDto(1, 1, 1, 2, 0), home.Manage); // 입사일 없음: cs1 + 대표원장
        Assert.Null((await nurse.Get<HomeDto>("/api/intranet/home")).Manage);
        Assert.Equal(15m, (await nurse.Get<HomeDto>("/api/intranet/home")).Leave!.Granted);
    }

    /* ---------- 주차 ---------- */

    [Fact]
    public async Task 차량을_등록하고_신청하면_관리자가_주차면을_배정한다()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, nurseId) = await _app.MemberAsync("nurse1", "Nurse", name: "김간호", phone: "010-1111-2222");
        var (doctor, doctorId) = await _app.MemberAsync("doctor1", "Doctor");

        await nurse.Post("/api/intranet/parking/requests", new { }).Status(HttpStatusCode.BadRequest); // 차량 먼저
        await nurse.Post("/api/intranet/parking/vehicles", new { plate = "abc" }).Status(HttpStatusCode.BadRequest);
        var car = await nurse.Post("/api/intranet/parking/vehicles", new { plate = "12가 3456", model = "흰색 아반떼" }).Ok<VehicleDto>();
        Assert.Equal("12가3456", car.Plate);
        await doctor.Post("/api/intranet/parking/vehicles", new { plate = "12가-3456" }).Status(HttpStatusCode.Conflict);

        await nurse.Post("/api/intranet/parking/requests", new { vehicleId = car.Id, note = "출퇴근 차량" }).Status(HttpStatusCode.Created);
        await nurse.Post("/api/intranet/parking/requests", new { }).Status(HttpStatusCode.Conflict); // 이미 신청

        await nurse.Post("/api/intranet/parking/spots", new { name = "B1-01" }).Status(HttpStatusCode.Forbidden);
        var spot = await owner.Post("/api/intranet/parking/spots", new { name = "B1-01", note = "엘리베이터 앞" }).Ok<SpotDto>();
        Assert.Single(await owner.Get<List<ParkingRequestDto>>("/api/intranet/parking/requests"));

        var a = await owner.Post("/api/intranet/parking/assignments", new { spotId = spot.Id, staffId = nurseId, startDate = "2026-11-16" }).Ok<ParkingAssignmentDto>();
        Assert.Equal("12가3456", a.Plate);
        Assert.Empty(await owner.Get<List<ParkingRequestDto>>("/api/intranet/parking/requests")); // 신청은 배정됨으로 닫힘

        var mine = await nurse.Get<MyParkingDto>("/api/intranet/parking/me");
        Assert.Equal("B1-01", mine.Assignment!.SpotName);
        Assert.Null(mine.Request);

        // 같은 주차면을 같은 기간에 다른 사람에게 줄 수 없음, 배정을 끝내면 가능
        await owner.Post("/api/intranet/parking/assignments", new { spotId = spot.Id, staffId = doctorId, startDate = "2026-12-01" }).Status(HttpStatusCode.Conflict);
        (await owner.Post($"/api/intranet/parking/assignments/{a.Id}/end", new { endDate = "2026-11-30" })).EnsureSuccessStatusCode();
        await owner.Post("/api/intranet/parking/assignments", new { spotId = spot.Id, staffId = doctorId, startDate = "2026-12-01" }).Status(HttpStatusCode.Created);

        var spots = await owner.Get<List<SpotDto>>("/api/intranet/parking/spots");
        Assert.Equal(nurseId, spots[0].Current!.StaffId);
        Assert.Equal(doctorId, spots[0].Next!.StaffId);
    }

    [Fact]
    public async Task 이중_주차는_번호_뒷자리로_차주를_찾는다()
    {
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse", name: "김간호", phone: "010-1111-2222");
        var (doctor, _) = await _app.MemberAsync("doctor1", "Doctor");
        await nurse.Post("/api/intranet/parking/vehicles", new { plate = "123나4567", model = "검정 쏘렌토" }).Status(HttpStatusCode.Created);

        var hit = Assert.Single(await doctor.Get<List<PlateLookupDto>>("/api/intranet/parking/lookup?q=4567"));
        Assert.Equal(("김간호", "010-1111-2222", "검정 쏘렌토"), (hit.OwnerName, hit.Phone, hit.Model));
        Assert.Empty(await doctor.Get<List<PlateLookupDto>>("/api/intranet/parking/lookup?q=9999"));
        Assert.Equal(HttpStatusCode.BadRequest, (await doctor.GetAsync("/api/intranet/parking/lookup?q=4")).StatusCode);
    }

    /* ---------- 보호 ---------- */

    [Fact]
    public async Task 인트라넷_화면과_API는_로그인이_필요하고_관리_화면은_관리자만()
    {
        var anon = _app.Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/intranet/home")).StatusCode);
        var page = await anon.GetAsync("/intranet/leave");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith("/login?next=", page.Headers.Location!.OriginalString);

        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse");
        foreach (var path in new[] { "/intranet/notices", "/intranet/leave", "/intranet/attendance", "/intranet/parking", "/intranet/people" })
            Assert.Contains($"<title>{path.Split('/')[^1]}</title>", await nurse.GetStringAsync(path));
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.GetAsync("/intranet/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.GetAsync("/intranet/attendance/kiosk")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.GetAsync("/api/intranet/settings")).StatusCode);

        var owner = await _app.OwnerAsync();
        Assert.Contains("<title>kiosk</title>", await owner.GetStringAsync("/intranet/attendance/kiosk"));
        var settings = await owner.Get<SettingsDto>("/api/intranet/settings");
        Assert.Equal([1, 2, 3, 4, 5], settings.WorkDays);
        Assert.Equal(new TimeOnly(9, 0), settings.WorkStart);
    }

    [Fact]
    public async Task 내장_공휴일은_한_번만_채우고_관리자가_지운_날은_다시_생기지_않는다()
    {
        var owner = await _app.OwnerAsync();
        var y2026 = await owner.Get<List<HolidayDto>>("/api/intranet/holidays?year=2026");
        Assert.Contains(y2026, h => h.Date == new DateOnly(2026, 10, 5) && h.Name.StartsWith("대체공휴일"));

        (await owner.DeleteAsync("/api/intranet/holidays/2026-05-01")).EnsureSuccessStatusCode(); // 근로자의 날 근무하는 병원
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await GoodMoNa02.Server.Intranet.ClinicSetup.EnsureAsync(db); // 서버 재시작과 같음
        }
        Assert.DoesNotContain(await owner.Get<List<HolidayDto>>("/api/intranet/holidays?year=2026"), h => h.Date == new DateOnly(2026, 5, 1));
        await owner.Post("/api/intranet/holidays", new { date = "2026-10-05", name = "중복" }).Status(HttpStatusCode.Conflict);
    }
}
