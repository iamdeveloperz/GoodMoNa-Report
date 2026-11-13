using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoodMoNa.Server.Auth;
using GoodMoNa.Server.Contracts;
using GoodMoNa.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace GoodMoNa.Server.Tests;

/// <summary>02 인트라넷이 발급한 출입증으로 간호사·원장·관리자 권한을 나누는지.</summary>
public sealed class AuthTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly byte[] FakeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0xFF, 0xD9];

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static async Task<T> Read<T>(HttpResponseMessage res) => (await res.Content.ReadFromJsonAsync<T>(Json))!;

    /// <summary>간호사가 환자 등록 → 오늘 방문 → QR → 폰 촬영까지 (보고 전)</summary>
    private async Task<(HttpClient Nurse, VisitDto Visit, string CaptureToken)> NurseWithPhoto()
    {
        var nurse = _app.ClientAs(StaffRoles.Nurse, "김간호", 17);
        var p = await Read<PatientSummaryDto>(await nurse.PostAsJsonAsync("/api/patients", new { name = "이서연", birth = "1992-07-08" }));
        var v = await Read<VisitDto>(await nurse.PostAsync($"/api/patients/{p.Id}/visits/today", null));
        var t = await Read<CaptureTokenDto>(await nurse.PostAsync($"/api/visits/{v.Id}/capture-tokens", null));

        // 폰은 로그인 없이 QR 토큰만으로 올림
        var phone = _app.ClientAs(null);
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(FakeJpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "photo", "photo.jpg");
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync($"/api/capture/{t.Token}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await phone.PostAsync($"/api/capture/{t.Token}/photo", form)).StatusCode);
        return (nurse, v, t.Token);
    }

    [Fact]
    public async Task 로그인하지_않으면_API는_401이고_화면은_로그인으로_보낸다()
    {
        var anon = _app.ClientAs(null);

        var api = await anon.GetAsync("/api/patients?q=김");
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal("로그인이 필요합니다.", (await api.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        var page = await anon.GetAsync("/report");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/login?next=%2Freport", page.Headers.Location!.ToString());

        // 역할 선택 첫 화면과 상태 확인은 열려 있음
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task 간호사는_보고할_수_있지만_원장_기능은_막힌다()
    {
        var (nurse, visit, _) = await NurseWithPhoto();

        Assert.Equal(HttpStatusCode.OK, (await nurse.PutAsJsonAsync($"/api/visits/{visit.Id}/report", new { progress = "Improved", memo = "정수리 좋아짐" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await nurse.GetAsync("/report")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.GetAsync("/api/director/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.GetAsync($"/api/visits/{visit.Id}/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.GetAsync($"/api/patients/{visit.Patient.Id}/history")).StatusCode);

        var page = await nurse.GetAsync("/director");
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Contains("김간호 (간호사)", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 원장은_결과를_확인하고_보고자는_로그인한_직원으로_남는다()
    {
        var (nurse, visit, _) = await NurseWithPhoto();
        (await nurse.PutAsJsonAsync($"/api/visits/{visit.Id}/report", new { progress = "Unchanged" })).EnsureSuccessStatusCode();

        var doctor = _app.ClientAs(StaffRoles.Doctor, "박원장", 3);
        var list = await doctor.GetFromJsonAsync<ReportListItemDto[]>("/api/director/reports", Json);
        Assert.Equal("김간호", Assert.Single(list!).ReportedBy);
        Assert.Equal(HttpStatusCode.OK, (await doctor.GetAsync("/director")).StatusCode);

        using var scope = _app.Services.CreateScope();
        var saved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Visits.SingleAsync(v => v.Id == visit.Id);
        Assert.Equal((17, "김간호"), (saved.ReportedById!.Value, saved.ReportedBy));
    }

    [Fact]
    public async Task 진료_직무가_아니면_환자_정보에_접근할_수_없다()
    {
        var staff = _app.ClientAs(StaffRoles.Staff, "최코디");
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/patients?q=김")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/report")).StatusCode);

        // 관리자는 기록 가져오기만 (환자 조회는 안 됨)
        var admin = _app.ClientAs(StaffRoles.Admin, "행정실장");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/import/files")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/import")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/patients?q=김")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _app.ClientAs(StaffRoles.Nurse).GetAsync("/api/import/files")).StatusCode);
    }

    [Fact]
    public async Task 다른_키로_서명했거나_만료된_출입증은_거절한다()
    {
        var forged = _app.CreateClient();
        forged.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _app.Token(StaffRoles.Owner, key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test" }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/api/patients?q=김")).StatusCode);

        var shortLived = _app.CreateClient();
        shortLived.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _app.Token(StaffRoles.Nurse, lifetime: TimeSpan.FromMinutes(15)));
        Assert.Equal(HttpStatusCode.OK, (await shortLived.GetAsync("/api/patients?q=김")).StatusCode);
        _app.Time.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.Unauthorized, (await shortLived.GetAsync("/api/patients?q=김")).StatusCode);
    }

    [Fact]
    public async Task 브라우저는_헤더_대신_gm_access_쿠키로_출입증을_보낸다()
    {
        var browser = _app.ClientAs(null);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/patients?q=김");
        req.Headers.Add("Cookie", $"gm_access={_app.Token(StaffRoles.Nurse)}");
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task 서버_PC에서_직접_열면_02의_로그인_화면으로_보낸다()
    {
        var res = await _app.ClientAs(null).GetAsync("/login?next=%2Fimport");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("http://localhost:5002/login?next=http%3A%2F%2Flocalhost%2Fimport", res.Headers.Location!.ToString());
    }
}
