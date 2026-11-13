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
}

/// <summary>테스트마다 빈 임시 데이터 폴더(SQLite·서명 키)로 서버를 띄웁니다. (01의 TestApp과 같은 방식)</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "goodmona02-tests", Guid.NewGuid().ToString("N"));

    // 쿠키 만료는 HttpClient가 실제 시계로 판단하므로, 시작 시각은 지금으로 둡니다.
    public TestTime Time { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // web 빌드 결과와 상관없이 화면 경로를 검사할 수 있도록 가짜 HTML을 둡니다.
        var webRoot = Path.Combine(DataRoot, "www");
        Directory.CreateDirectory(webRoot);
        foreach (var page in new[] { "login", "signup", "intranet", "intranet-admin" })
            File.WriteAllText(Path.Combine(webRoot, $"{page}.html"), $"<!doctype html><title>{page}</title>");
        builder.UseWebRoot(webRoot);

        builder.UseEnvironment("Testing");
        builder.UseSetting("App:DataRoot", DataRoot);
        builder.UseSetting("App:Auth:AuthRequestsPerMinute", "1000");
        builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time)));
    }

    /// <summary>쿠키를 따로 들고 다니는 브라우저 하나. 리다이렉트는 따라가지 않습니다(화면 보호 검사용).</summary>
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataRoot, recursive: true); } catch (IOException) { /* 임시 폴더라 남아도 무방 */ }
    }
}
