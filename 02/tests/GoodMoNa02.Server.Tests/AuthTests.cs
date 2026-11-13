using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace GoodMoNa02.Server.Tests;

/// <summary>가입(승인 필수) · 로그인 · 출입증 · 갱신 · 직원 관리.</summary>
public sealed class AuthTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string Pw = "goodmona-2026!";

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    /* ---------- 도우미 ---------- */

    /// <summary>첫 관리자(대표원장)로 로그인된 브라우저</summary>
    private async Task<HttpClient> OwnerBrowser()
    {
        var b = _app.Browser();
        var res = await b.PostAsJsonAsync("/api/auth/bootstrap", new { loginId = "owner", name = "대표원장", password = Pw });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return b;
    }

    private async Task<int> Signup(string loginId, string role, string name = "김간호")
    {
        var res = await _app.Browser().PostAsJsonAsync("/api/auth/signup", new { loginId, name, password = Pw, requestedRole = role, department = "진료부" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var staff = await Staff(await OwnerOrExisting());
        return staff.Single(s => s.LoginId == loginId).Id;
    }

    private HttpClient? _owner;
    private async Task<HttpClient> OwnerOrExisting() => _owner ??= await OwnerBrowser();

    private static async Task<StaffDto[]> Staff(HttpClient admin, string? status = null) =>
        (await admin.GetFromJsonAsync<StaffDto[]>($"/api/admin/staff{(status is null ? "" : $"?status={status}")}", Json))!;

    private static Task<HttpResponseMessage> Login(HttpClient b, string loginId, string password = Pw) =>
        b.PostAsJsonAsync("/api/auth/login", new { loginId, password });

    private async Task<HttpClient> ActiveBrowser(string loginId, string role)
    {
        var id = await Signup(loginId, role);
        (await (await OwnerOrExisting()).PostAsJsonAsync($"/api/admin/staff/{id}/approve", new { })).EnsureSuccessStatusCode();
        var b = _app.Browser();
        (await Login(b, loginId)).EnsureSuccessStatusCode();
        return b;
    }

    private static async Task<string> Detail(HttpResponseMessage res) => (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;

    private static string? SetCookie(HttpResponseMessage res, string name) =>
        res.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..]
            : null;

    /* ---------- 가입 · 승인 ---------- */

    [Fact]
    public async Task 가입하면_승인_대기이고_승인_전에는_로그인할_수_없다()
    {
        var res = await _app.Browser().PostAsJsonAsync("/api/auth/signup", new { loginId = "Nurse.Kim", name = "김간호", password = Pw, requestedRole = "Nurse" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal(new SignupResultDto("nurse.kim", StaffStatus.Pending), await res.Content.ReadFromJsonAsync<SignupResultDto>(Json));

        var login = await Login(_app.Browser(), "nurse.kim");
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Contains("승인 대기", await Detail(login));

        // 비밀번호가 틀리면 상태를 알려 주지 않습니다.
        var wrong = await Login(_app.Browser(), "nurse.kim", "wrong-password");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task 관리자가_직무를_정해_승인하면_로그인되고_출입증에_직무가_담긴다()
    {
        var owner = await OwnerBrowser();
        _owner = owner;
        var id = await Signup("kim", "Staff");
        var pending = Assert.Single(await Staff(owner, "Pending"));
        Assert.Equal((StaffRole.Staff, "진료부"), (pending.RequestedRole, pending.Department));

        // 본인은 '직원'으로 신청했지만 승인하는 사람이 '간호사'로 바꿔서 승인
        var approved = await owner.PostAsJsonAsync($"/api/admin/staff/{id}/approve", new { role = "Nurse" });
        Assert.Equal(StaffRole.Nurse, (await approved.Content.ReadFromJsonAsync<StaffDto>(Json))!.Role);

        var b = _app.Browser();
        var login = await Login(b, "kim");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(StaffRole.Nurse, (await b.GetFromJsonAsync<MeDto>("/api/auth/me", Json))!.Role);

        // 출입증(JWT): 02의 공개키로 서명이 맞고, 01이 쓰는 값(sub·name·role·iss·aud)이 들어 있음
        var token = SetCookie(login, "gm_access")!;
        var jwks = new JsonWebKeySet(await b.GetStringAsync("/.well-known/jwks.json"));
        var check = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "goodmona-intranet",
            ValidAudience = "goodmona",
            IssuerSigningKeys = jwks.GetSigningKeys(),
            ValidateLifetime = false,
        });
        Assert.True(check.IsValid);
        Assert.Equal(("Nurse", "김간호", id.ToString()), ((string)check.Claims["role"], (string)check.Claims["name"], (string)check.Claims["sub"]));
        Assert.DoesNotContain(check.Claims.Keys, k => k.Contains("password", StringComparison.OrdinalIgnoreCase));

        var config = await b.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");
        Assert.Equal("goodmona-intranet", config.GetProperty("issuer").GetString());
    }

    [Fact]
    public async Task 반려하면_사유와_함께_로그인이_거절된다()
    {
        _owner = await OwnerBrowser();
        var id = await Signup("temp", "Nurse");
        (await _owner.PostAsJsonAsync($"/api/admin/staff/{id}/reject", new { reason = "재직자 명단에 없음" })).EnsureSuccessStatusCode();

        var login = await Login(_app.Browser(), "temp");
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Contains("재직자 명단에 없음", await Detail(login));
    }

    [Fact]
    public async Task 가입_입력값을_검사한다()
    {
        async Task<HttpStatusCode> Try(object body) => (await _app.Browser().PostAsJsonAsync("/api/auth/signup", body)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { loginId = "ab", name = "김", password = Pw, requestedRole = "Nurse" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { loginId = "kim", name = "김", password = "short", requestedRole = "Nurse" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { loginId = "kimnurse", name = "김", password = "kimnurse123", requestedRole = "Nurse" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { loginId = "kim", name = "김", password = Pw, requestedRole = "Janitor" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(new { loginId = "kim", name = "", password = Pw, requestedRole = "Nurse" }));
        Assert.Equal(HttpStatusCode.Created, await Try(new { loginId = "kim", name = "김", password = Pw, requestedRole = "Nurse" }));
        Assert.Equal(HttpStatusCode.Conflict, await Try(new { loginId = "KIM", name = "김", password = Pw, requestedRole = "Doctor" }));
    }

    /* ---------- 첫 관리자 ---------- */

    [Fact]
    public async Task 첫_관리자는_관리자가_없을_때_서버_PC에서만_만들_수_있다()
    {
        var b = _app.Browser();
        Assert.True((await b.GetFromJsonAsync<BootstrapStatusDto>("/api/auth/bootstrap", Json))!.Available);

        // Cloudflare를 거친 외부 요청은 안 됨
        using var remote = new HttpRequestMessage(HttpMethod.Post, "/api/auth/bootstrap")
        {
            Content = JsonContent.Create(new { loginId = "hacker", name = "x", password = Pw }),
        };
        remote.Headers.Add("Cf-Connecting-IP", "203.0.113.9");
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SendAsync(remote)).StatusCode);

        await OwnerBrowser();

        // 관리자가 생긴 뒤에는 서버 PC에서도 안 됨
        Assert.False((await b.GetFromJsonAsync<BootstrapStatusDto>("/api/auth/bootstrap", Json))!.Available);
        var second = await b.PostAsJsonAsync("/api/auth/bootstrap", new { loginId = "owner2", name = "x", password = Pw });
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
    }

    /* ---------- 권한 ---------- */

    [Fact]
    public async Task 직원_관리는_대표원장과_관리자만_할_수_있다()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Browser().GetAsync("/api/admin/staff")).StatusCode);

        var nurse = await ActiveBrowser("nurse", "Nurse");
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.GetAsync("/api/admin/staff")).StatusCode);

        var admin = await ActiveBrowser("office", "Admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/staff")).StatusCode);
    }

    [Fact]
    public async Task 화면은_로그인과_직무를_확인한다()
    {
        var anon = _app.Browser();
        var redirect = await anon.GetAsync("/intranet");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/login?next=%2Fintranet", redirect.Headers.Location!.ToString());

        var nurse = await ActiveBrowser("nurse", "Nurse");
        Assert.Equal(HttpStatusCode.OK, (await nurse.GetAsync("/intranet")).StatusCode);
        var denied = await nurse.GetAsync("/intranet/admin");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("김간호 (간호사)", await denied.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await (await OwnerOrExisting()).GetAsync("/intranet/admin")).StatusCode);
    }

    [Fact]
    public async Task 마지막_관리자는_중지하거나_직무를_내릴_수_없다()
    {
        var owner = await OwnerOrExisting();
        var me = (await owner.GetFromJsonAsync<MeDto>("/api/auth/me", Json))!;

        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync($"/api/admin/staff/{me.Id}/role", new { role = "Nurse" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync($"/api/admin/staff/{me.Id}/disable", null)).StatusCode);
    }

    /* ---------- 잠금 · 갱신 · 퇴사 ---------- */

    [Fact]
    public async Task 비밀번호를_5번_틀리면_10분_동안_잠긴다()
    {
        await ActiveBrowser("nurse", "Nurse");
        var b = _app.Browser();
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Login(b, "nurse", "wrong-pass")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(b, "nurse", "wrong-pass")).StatusCode);

        // 잠긴 동안에는 맞는 비밀번호도 거절
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(b, "nurse")).StatusCode);

        _app.Time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.OK, (await Login(b, "nurse")).StatusCode);
    }

    [Fact]
    public async Task 출입증이_만료되면_갱신하고_퇴사_처리하면_갱신이_막힌다()
    {
        var nurse = await ActiveBrowser("nurse", "Nurse");
        var nurseId = (await nurse.GetFromJsonAsync<MeDto>("/api/auth/me", Json))!.Id;

        _app.Time.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.Unauthorized, (await nurse.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await nurse.PostAsync("/api/auth/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await nurse.GetAsync("/api/auth/me")).StatusCode);

        // 관리자 출입증도 만료됐으니 갱신 후 퇴사 처리
        var owner = await OwnerOrExisting();
        (await owner.PostAsync("/api/auth/refresh", null)).EnsureSuccessStatusCode();
        (await owner.PostAsync($"/api/admin/staff/{nurseId}/disable", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Unauthorized, (await nurse.PostAsync("/api/auth/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Login(_app.Browser(), "nurse")).StatusCode);
    }

    [Fact]
    public async Task 직무를_바꾸면_다음_갱신부터_출입증에_반영된다()
    {
        var b = await ActiveBrowser("kim", "Nurse");
        var id = (await b.GetFromJsonAsync<MeDto>("/api/auth/me", Json))!.Id;
        (await (await OwnerOrExisting()).PutAsJsonAsync($"/api/admin/staff/{id}/role", new { role = "Doctor" })).EnsureSuccessStatusCode();

        var refreshed = await b.PostAsync("/api/auth/refresh", null);
        Assert.Equal(StaffRole.Doctor, (await refreshed.Content.ReadFromJsonAsync<MeDto>(Json))!.Role);
    }

    [Fact]
    public async Task 이미_바뀐_갱신_토큰이_다시_쓰이면_그_직원의_세션을_모두_끊는다()
    {
        await ActiveBrowser("nurse", "Nurse");
        var raw = _app.CreateClient(new() { HandleCookies = false, AllowAutoRedirect = false });
        var login = await Login(raw, "nurse");
        var first = SetCookie(login, "gm_refresh")!;

        async Task<HttpResponseMessage> RefreshWith(string token)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
            req.Headers.Add("Cookie", $"gm_refresh={token}");
            return await raw.SendAsync(req);
        }

        var rotated = await RefreshWith(first);
        var second = SetCookie(rotated, "gm_refresh")!;
        Assert.NotEqual(first, second);

        // 30초 안: 다른 탭이 동시에 갱신한 경우로 보고 허용
        _app.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, (await RefreshWith(first)).StatusCode);

        // 한참 뒤 옛 토큰 재사용 = 도난 의심 → 새 토큰까지 모두 끊김
        _app.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshWith(first)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshWith(second)).StatusCode);
    }

    [Fact]
    public async Task 로그아웃하면_갱신할_수_없다()
    {
        var b = await ActiveBrowser("nurse", "Nurse");
        Assert.Equal(HttpStatusCode.NoContent, (await b.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.PostAsync("/api/auth/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/api/auth/me")).StatusCode);
    }
}
