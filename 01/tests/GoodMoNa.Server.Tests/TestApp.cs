using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using GoodMoNa.Server.Transcription;

namespace GoodMoNa.Server.Tests;

/// <summary>시간을 마음대로 움직일 수 있는 시계 (QR 만료 테스트용)</summary>
public sealed class TestTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// Whisper 대신 쓰는 가짜 변환기. <see cref="Gate"/>를 열기 전까지 변환이 끝나지 않으므로
/// "변환 도중 삭제" 같은 경합 상황을 재현할 수 있습니다.
/// </summary>
public sealed class FakeSpeechToText : ISpeechToText
{
    public TaskCompletionSource Gate { get; private set; } = Open();
    public string Text { get; set; } = "정수리 밀도 좋아졌고 미녹시딜 그대로 유지합니다.";
    public bool Fail { get; set; }
    public int Calls;
    public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource Open()
    {
        var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        t.SetResult();
        return t;
    }

    public void Hold()
    {
        Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task<TranscriptionResult> TranscribeAsync(string wavPath, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        // 실제 Whisper처럼 변환 중에는 파일을 열어 둡니다 (삭제 시 파일 잠금 재현).
        await using var _ = File.OpenRead(wavPath);
        Started.TrySetResult();
        await Gate.Task.WaitAsync(ct);
        if (Fail) throw new InvalidOperationException("가짜 변환 실패");
        return new TranscriptionResult(Text, [new TranscriptSegment(0, 1000, Text)], "fake", "test", 5);
    }
}

/// <summary>테스트마다 빈 임시 데이터 폴더(SQLite·사진·녹음)로 서버를 띄웁니다.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "goodmona-tests", Guid.NewGuid().ToString("N"));

    // 2026-11-13 10:00 KST
    public TestTime Time { get; } = new(new DateTimeOffset(2026, 11, 13, 1, 0, 0, TimeSpan.Zero));

    public FakeSpeechToText Stt { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // web 빌드 결과와 상관없이 화면 라우팅을 검사할 수 있도록, 페이지마다 이름만 적힌 가짜 HTML을 둡니다.
        var webRoot = Path.Combine(DataRoot, "www");
        Directory.CreateDirectory(webRoot);
        foreach (var page in new[] { "index", "report", "director", "capture" })
            File.WriteAllText(Path.Combine(webRoot, $"{page}.html"), $"<!doctype html><title>{page}</title>");
        builder.UseWebRoot(webRoot);

        builder.UseEnvironment("Testing");
        builder.UseSetting("App:DataRoot", DataRoot);
        builder.UseSetting("App:SeedDemoData", "false");
        builder.UseSetting("App:PublicBaseUrl", "https://report.example.test");
        builder.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time));
            s.Replace(ServiceDescriptor.Singleton<ISpeechToText>(Stt));
        });
    }

    protected override void Dispose(bool disposing)
    {
        Stt.Gate.TrySetResult();
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataRoot, recursive: true); } catch (IOException) { /* 임시 폴더라 남아도 무방 */ }
    }
}
