using System.Text.Json.Serialization;
using GoodMoNa03.Server.Auth;
using GoodMoNa03.Server.Data;
using GoodMoNa03.Server.Endpoints;
using GoodMoNa03.Server.Infrastructure;
using GoodMoNa03.Server.Media;
using GoodMoNa03.Server.Pipeline;
using GoodMoNa03.Server.Writing;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "GoodMoNa03");

// 재배포해도 남아야 하는 설정(AI 모드·API 키·ffmpeg 경로 등)은 데이터 폴더의 appsettings.local.json에 둡니다(02와 같은 방식).
if (!builder.Environment.IsEnvironment("Testing"))
{
    var dataRoot = new DataPaths(builder.Configuration.GetSection(AppOptions.Section).Get<AppOptions>() ?? new AppOptions()).Root;
    builder.Configuration.AddJsonFile(Path.Combine(dataRoot, "appsettings.local.json"), optional: true, reloadOnChange: false);
}

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.Section));
builder.Services.AddSingleton(sp => new DataPaths(sp.GetRequiredService<IOptions<AppOptions>>().Value));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ClinicClock>();
builder.Services.AddDbContext<AppDbContext>((sp, o) => o.UseSqlite(sp.GetRequiredService<DataPaths>().ConnectionString));

// 촬영본 → (ffmpeg 무음 감지 + Whisper 받아쓰기) → 자막·컷·초안·점검 → 미리보기 → 승인 → 완성본. 한 번에 하나씩.
builder.Services.AddSingleton<IVideoTool, FfmpegVideoTool>();
builder.Services.AddSingleton<ISpeechToText, WhisperSpeechToText>();
builder.Services.AddSingleton<DraftQueue>();
builder.Services.AddHostedService<DraftWorker>();

// 제목·훅 초안과 표현 점검 AI: 꺼짐(기본) / 서버 PC Ollama / Claude API
builder.Services.AddHttpClient(OllamaAiWriter.HttpClientName, (sp, c) =>
    c.Timeout = TimeSpan.FromSeconds(Math.Max(5, sp.GetRequiredService<IOptions<AppOptions>>().Value.Ai.TimeoutSeconds)));
builder.Services.AddSingleton<IAiWriter>(sp =>
{
    var ai = sp.GetRequiredService<IOptions<AppOptions>>().Value.Ai;
    if (ai.ClaudeEnabled) return ActivatorUtilities.CreateInstance<ClaudeAiWriter>(sp);
    if (ai.OllamaEnabled) return ActivatorUtilities.CreateInstance<OllamaAiWriter>(sp);
    return new NoAiWriter();
});
builder.Services.AddScoped<Copywriter>();

// 로그인: 02 인트라넷 서버가 발급한 출입증으로 편집(직원)·승인(원장)을 나눔
builder.Services.AddStaffAuth();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UsePageGate();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthorization();

var editor = app.MapGroup("").RequireAuthorization(Policies.Editor);
editor.MapEditorEndpoints();
editor.MapSystemEndpoints();
app.MapGroup("").RequireAuthorization(Policies.Approver).MapApproverEndpoints();
app.MapGet("/login", PageGate.LocalLoginRedirect);
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// 화면: /shorts (올리기·목록) · /shorts/review?id= (검토·승인)
app.MapGet("/", () => Results.Redirect("/shorts"));
app.MapFallbackToFile("/shorts", "shorts.html");
app.MapFallbackToFile("/shorts/review", "review.html");

app.Run();

public partial class Program;
