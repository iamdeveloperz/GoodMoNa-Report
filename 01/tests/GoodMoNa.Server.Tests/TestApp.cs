using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoodMoNa.Server.Tests;

/// <summary>시간을 마음대로 움직일 수 있는 시계 (QR 만료 테스트용)</summary>
public sealed class TestTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>테스트마다 빈 임시 데이터 폴더(SQLite·사진)로 서버를 띄웁니다.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), "goodmona-tests", Guid.NewGuid().ToString("N"));

    // 2026-11-13 10:00 KST
    public TestTime Time { get; } = new(new DateTimeOffset(2026, 11, 13, 1, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("App:DataRoot", _dataRoot);
        builder.UseSetting("App:SeedDemoData", "false");
        builder.UseSetting("App:PublicBaseUrl", "https://report.example.test");
        builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time)));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataRoot, recursive: true); } catch (IOException) { /* 임시 폴더라 남아도 무방 */ }
    }
}
