using System.Net;
using System.Net.Http.Json;

namespace GoodMoNa02.Server.Tests;

/// <summary>골격이 제대로 뜨는지만 확인합니다. 기능을 만들면 기능별 테스트 파일을 추가하세요.</summary>
public sealed class SmokeTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _http;

    public SmokeTests() => _http = _app.CreateClient();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task 서버가_뜨고_healthz가_ok다()
    {
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task 오늘은_병원_시간대_기준이다()
    {
        var info = await _http.GetFromJsonAsync<Info>("/api/info");
        Assert.Equal(new DateOnly(2026, 11, 13), info!.Today);
    }

    [Fact]
    public async Task 첫_화면이_열린다()
    {
        Assert.Contains("<title>index</title>", await _http.GetStringAsync("/"));
    }

    private sealed record Info(string Project, DateOnly Today);
}
