using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoodMoNa02.Server.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoodMoNa02.Server.Tests;

/// <summary>시간을 마음대로 움직일 수 있는 시계 (출입증 만료·잠금 해제 테스트용)</summary>
public sealed class TestTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;

    /// <summary>병원 시각(KST)으로 맞춥니다.</summary>
    public void SetKst(int year, int month, int day, int hour = 9, int minute = 0) =>
        Now = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(9));
}

/// <summary>테스트마다 빈 임시 데이터 폴더(SQLite·서명 키)로 서버를 띄웁니다. (01의 TestApp과 같은 방식)</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    public const string Password = "goodmona-2026!";

    public static readonly string[] Pages = ["login", "signup", "intranet", "intranet-admin", "notices", "leave", "attendance", "kiosk", "parking", "people", "settings", "ask", "knowledge"];

    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "goodmona02-tests", Guid.NewGuid().ToString("N"));

    // 쿠키 만료는 HttpClient가 실제 시계로 판단하므로, 시작 시각은 지금으로 둡니다.
    public TestTime Time { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>서버를 띄우기 전에 서비스를 바꿔 끼웁니다(가짜 AI 등). 첫 요청 전에 설정하세요.</summary>
    public Action<IServiceCollection>? TestServices { get; set; }

    /// <summary>서버 설정 덮어쓰기 (예: App:Assistant:Mode). 첫 요청 전에 넣으세요.</summary>
    public Dictionary<string, string> TestSettings { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // web 빌드 결과와 상관없이 화면 경로를 검사할 수 있도록 가짜 HTML을 둡니다.
        var webRoot = Path.Combine(DataRoot, "www");
        Directory.CreateDirectory(webRoot);
        foreach (var page in Pages)
            File.WriteAllText(Path.Combine(webRoot, $"{page}.html"), $"<!doctype html><title>{page}</title>");
        builder.UseWebRoot(webRoot);

        builder.UseEnvironment("Testing");
        builder.UseSetting("App:DataRoot", DataRoot);
        builder.UseSetting("App:Auth:AuthRequestsPerMinute", "1000");
        foreach (var (key, value) in TestSettings) builder.UseSetting(key, value);
        builder.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time));
            TestServices?.Invoke(s);
        });
    }

    /// <summary>쿠키를 따로 들고 다니는 브라우저 하나. 리다이렉트는 따라가지 않습니다(화면 보호 검사용).</summary>
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /* ---------- 직원 만들기 (업무 기능 테스트용) ---------- */

    private HttpClient? _owner;

    /// <summary>첫 관리자(대표원장 "owner")로 로그인된 브라우저. 테스트 안에서 한 번만 만듭니다.</summary>
    public async Task<HttpClient> OwnerAsync()
    {
        if (_owner is not null) return _owner;
        var b = Browser();
        (await b.PostAsJsonAsync("/api/auth/bootstrap", new { loginId = "owner", name = "대표원장", password = Password })).EnsureSuccessStatusCode();
        return _owner = b;
    }

    /// <summary>가입 → 승인 → 로그인한 직원 브라우저와 직원번호. 입사일을 주면 관리자가 등록해 둡니다.</summary>
    public async Task<(HttpClient Browser, int Id)> MemberAsync(string loginId, string role, string? hireDate = null, string? name = null, string? phone = null)
    {
        var owner = await OwnerAsync();
        var res = await Browser().PostAsJsonAsync("/api/auth/signup",
            new { loginId, name = name ?? loginId, password = Password, requestedRole = role, department = "진료부", phone });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var staff = (await owner.GetFromJsonAsync<StaffDto[]>("/api/admin/staff?status=Pending", Json))!;
        var id = staff.Single(s => s.LoginId == loginId).Id;
        (await owner.PostAsJsonAsync($"/api/admin/staff/{id}/approve", new { })).EnsureSuccessStatusCode();
        if (hireDate is not null)
            (await owner.PutAsJsonAsync($"/api/admin/staff/{id}/profile", new { department = "진료부", phone, hireDate })).EnsureSuccessStatusCode();

        var b = Browser();
        (await b.PostAsJsonAsync("/api/auth/login", new { loginId, password = Password })).EnsureSuccessStatusCode();
        return (b, id);
    }

    public static async Task<string> Detail(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataRoot, recursive: true); } catch (IOException) { /* 임시 폴더라 남아도 무방 */ }
    }
}

/// <summary>JSON 요청·응답 줄임 도우미</summary>
public static class HttpJson
{
    public static async Task<T> Get<T>(this HttpClient http, string url)
    {
        var res = await http.GetAsync(url);
        Assert.True(res.IsSuccessStatusCode, $"GET {url} → {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>(TestApp.Json))!;
    }

    public static Task<HttpResponseMessage> Post(this HttpClient http, string url, object? body = null) =>
        http.PostAsJsonAsync(url, body ?? new { }, TestApp.Json);

    public static Task<HttpResponseMessage> Put(this HttpClient http, string url, object body) =>
        http.PutAsJsonAsync(url, body, TestApp.Json);

    public static async Task<T> Ok<T>(this Task<HttpResponseMessage> call)
    {
        var res = await call;
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>(TestApp.Json))!;
    }

    public static async Task<HttpResponseMessage> Status(this Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var res = await call;
        Assert.True(res.StatusCode == expected, $"기대 {(int)expected}, 실제 {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return res;
    }
}
