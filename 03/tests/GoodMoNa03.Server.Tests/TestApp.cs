using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoodMoNa03.Server.Auth;
using GoodMoNa03.Server.Contracts;
using GoodMoNa03.Server.Data;
using GoodMoNa03.Server.Editing;
using GoodMoNa03.Server.Media;
using GoodMoNa03.Server.Writing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace GoodMoNa03.Server.Tests;

public sealed class TestTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>ffmpeg 대신: 정해 둔 영상 정보·무음을 돌려주고, 렌더 요청과 자막 파일을 기록합니다.</summary>
public sealed class FakeVideoTool : IVideoTool
{
    public ProbeResult Probe { get; set; } = new(20_000, 1080, 1920, true);
    public List<TimeRange> Silences { get; set; } = [new(0, 800), new(6_000, 7_000), new(19_500, 20_000)];
    public ConcurrentQueue<RenderRequest> Renders { get; } = new();
    public ConcurrentQueue<string> AssFiles { get; } = new();
    public bool FailRender { get; set; }

    public Task<ProbeResult> ProbeAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new FfmpegException("원본이 없습니다.");
        return Task.FromResult(Probe);
    }

    public Task ExtractAudioAsync(string sourcePath, string wavPath, CancellationToken ct) => File.WriteAllBytesAsync(wavPath, [0, 1, 2, 3], ct);

    public Task<IReadOnlyList<TimeRange>> DetectSilenceAsync(string sourcePath, int noiseDb, int minSilenceMs, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TimeRange>>(Silences);

    public async Task RenderAsync(RenderRequest request, CancellationToken ct)
    {
        if (FailRender) throw new FfmpegException("영상을 만들지 못했습니다. 가짜 실패");
        AssFiles.Enqueue(await File.ReadAllTextAsync(Path.Combine(request.WorkDir, request.AssFile), ct));
        Renders.Enqueue(request);
        await File.WriteAllBytesAsync(request.OutputPath, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], ct);
    }

    public Task<string?> VersionAsync(CancellationToken ct) => Task.FromResult<string?>("ffmpeg version fake");
}

/// <summary>Whisper 대신: 정해 둔 단어 목록.</summary>
public sealed class FakeSpeechToText : ISpeechToText
{
    public List<Word> Words { get; set; } = Sentence(1_000,
        "미녹시딜 바르기 전에 꼭 알아야 할 것 세 가지입니다.", "첫째 부작용 없이 바로 효과가 나는 약은 없습니다.", "둘째 하루 두 번 꾸준히 바르세요.");

    public Task<WordTranscript> TranscribeAsync(string wavPath, CancellationToken ct) =>
        Task.FromResult(new WordTranscript(Words, "fake", "test", 5));

    /// <summary>문장들을 단어마다 400ms, 문장 사이 1초 쉼으로 배치</summary>
    public static List<Word> Sentence(int startMs, params string[] sentences)
    {
        var words = new List<Word>();
        var t = startMs;
        foreach (var s in sentences)
        {
            foreach (var w in s.Split(' '))
            {
                words.Add(new Word(t, t + 350, w));
                t += 400;
            }
            t += 1_000;
        }
        return words;
    }
}

/// <summary>AI 대신: 정해 둔 JSON을 돌려줌.</summary>
public sealed class FakeAiWriter : IAiWriter
{
    public bool Enabled { get; set; } = true;
    public string Label => "가짜 AI";
    public bool Fail { get; set; }
    public string CopyJson { get; set; } = """
        ```json
        {"hooks":["미녹시딜 3가지","바르기 전 필수","이것만 알면 됨","네번째는 버림"],
         "titles":["미녹시딜 바르기 전 꼭 알아야 할 3가지","탈모약 바르는 법, 의사가 정리했습니다 정말 아주 아주 아주 긴 제목이라 잘려야 합니다"],
         "summary":"미녹시딜 사용 전 알아야 할 점을 정리합니다.","hashtags":["미녹시딜","#탈모 치료"],"emphasis":["미녹시딜","없는단어"]}
        ```
        """;
    public string RiskJson { get; set; } = """{"risks":[{"text":"부작용 없이 바로 효과가 나는 약은 없습니다","reason":"부작용 언급 확인"}]}""";

    public Task<string> CompleteJsonAsync(string system, string user, CancellationToken ct)
    {
        if (Fail) throw new HttpRequestException("연결 실패");
        return Task.FromResult(system == Copywriter.RiskSystem ? RiskJson : CopyJson);
    }
}

/// <summary>테스트마다 빈 임시 데이터 폴더로 서버를 띄웁니다.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), "goodmona03-tests", Guid.NewGuid().ToString("N"));

    // 2026-11-16 10:00 KST
    public TestTime Time { get; } = new(new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.Zero));

    public FakeVideoTool Video { get; } = new();
    public FakeSpeechToText Stt { get; } = new();
    /// <summary>null이면 설정(App:Ai)대로 진짜 AI를 씀(실제 도구 시험용)</summary>
    public IAiWriter? Ai { get; set; } = new NoAiWriter();

    /// <summary>true면 가짜 대신 실제 ffmpeg·Whisper를 씀(실제 도구 시험용)</summary>
    public bool RealTools { get; init; }

    /// <summary>실제 도구 시험에서 넘길 설정(App:Ffmpeg:FfmpegPath 등)</summary>
    public Dictionary<string, string> Settings { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var webRoot = Path.Combine(DataRoot, "www");
        Directory.CreateDirectory(webRoot);
        foreach (var page in new[] { "shorts", "review" })
            File.WriteAllText(Path.Combine(webRoot, $"{page}.html"), $"<!doctype html><title>{page}</title>");
        builder.UseWebRoot(webRoot);

        builder.UseEnvironment("Testing");
        builder.UseSetting("App:DataRoot", DataRoot);
        builder.UseSetting("App:Copy:LandingUrl", "https://landing.example.test/");
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);
        builder.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Singleton<TimeProvider>(Time));
            if (!RealTools)
            {
                s.Replace(ServiceDescriptor.Singleton<IVideoTool>(Video));
                s.Replace(ServiceDescriptor.Singleton<ISpeechToText>(Stt));
            }
            if (Ai is { } ai) s.Replace(ServiceDescriptor.Singleton(ai));

            s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                var config = new OpenIdConnectConfiguration { Issuer = Issuer };
                config.SigningKeys.Add(SigningKey);
                o.Configuration = config;
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
            });
        });
    }

    /* ---------- 출입증 (02가 발급하는 것과 같은 모양의 JWT) ---------- */

    public const string Issuer = "goodmona-intranet";
    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test" };

    public string Token(string role, int staffId = 1, string name = "테스트")
    {
        var now = Time.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = "goodmona",
            IssuedAt = now,
            NotBefore = now,
            Expires = now + TimeSpan.FromDays(365),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object> { ["sub"] = staffId.ToString(), ["name"] = name, ["role"] = role },
        });
    }

    public HttpClient ClientAs(string? role, string name = "테스트", int staffId = 1)
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Authorization = role is null ? null : new AuthenticationHeaderValue("Bearer", Token(role, staffId, name));
        return c;
    }

    public HttpClient Staff => ClientAs(StaffRoles.Staff, "박마케팅", 30);
    public HttpClient Doctor => ClientAs(StaffRoles.Doctor, "이원장", 2);

    /* ---------- 공통 동작 ---------- */

    public static async Task<T> Read<T>(HttpResponseMessage res)
    {
        if (!res.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public static Task<HttpResponseMessage> UploadAsync(HttpClient c, string name = "촬영본.mp4", string type = "video/mp4")
    {
        var content = new ByteArrayContent([0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70, 0x6D, 0x70, 0x34, 0x32]);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return c.PostAsync($"/api/shorts/sources?name={Uri.EscapeDataString(name)}", content);
    }

    /// <summary>백그라운드 작업이 끝날 때까지(분석·렌더 중이 아닐 때까지) 기다립니다.</summary>
    public static async Task<DraftDto> WaitAsync(HttpClient c, int id, int maxPolls = 200)
    {
        for (var i = 0; i < maxPolls; i++)
        {
            var d = await Read<DraftDto>(await c.GetAsync($"/api/shorts/drafts/{id}"));
            if (d.Status is not (DraftStatus.Processing or DraftStatus.Rendering or DraftStatus.Approved)) return d;
            await Task.Delay(25);
        }
        throw new TimeoutException("초안 처리가 끝나지 않았습니다.");
    }

    public async Task<DraftDto> UploadAndWaitAsync(HttpClient? c = null)
    {
        c ??= Staff;
        var d = await Read<DraftDto>(await UploadAsync(c));
        return await WaitAsync(c, d.Id);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
