using System.Net;
using System.Net.Http.Json;

namespace GoodMoNa02.Server.Tests;

public sealed class SmokeTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _http;

    public SmokeTests() => _http = _app.Browser();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task 서버가_뜨고_healthz가_ok다()
    {
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task 오늘은_병원_시간대_기준이다()
    {
        _app.Time.Now = new DateTimeOffset(2026, 11, 13, 16, 0, 0, TimeSpan.Zero); // 11-14 01:00 KST
        var info = await _http.GetFromJsonAsync<Info>("/api/info");
        Assert.Equal(new DateOnly(2026, 11, 14), info!.Today);
    }

    [Fact]
    public async Task 로그인_가입_화면은_누구나_열린다()
    {
        Assert.Contains("<title>login</title>", await _http.GetStringAsync("/login"));
        Assert.Contains("<title>signup</title>", await _http.GetStringAsync("/signup"));
    }

    private sealed record Info(string Project, DateOnly Today);
}
