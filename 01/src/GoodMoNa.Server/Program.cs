using System.Text.Json.Serialization;
using GoodMoNa.Server.Data;
using GoodMoNa.Server.Endpoints;
using GoodMoNa.Server.Hubs;
using GoodMoNa.Server.Infrastructure;
using GoodMoNa.Server.Transcription;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Windows 서비스로 등록해 두면 PC가 켜질 때 자동으로 뜹니다. (콘솔 실행에는 영향 없음)
builder.Services.AddWindowsService(o => o.ServiceName = "GoodMoNa");

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.Section));
builder.Services.AddSingleton(sp => new DataPaths(sp.GetRequiredService<IOptions<AppOptions>>().Value));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ClinicClock>();
builder.Services.AddSingleton<MediaStorage>();
builder.Services.AddDbContext<AppDbContext>((sp, o) => o.UseSqlite(sp.GetRequiredService<DataPaths>().ConnectionString));

// 원장 녹음 → Whisper 자동 변환 (한 번에 하나씩, 백그라운드)
builder.Services.AddSingleton<ISpeechToText, WhisperSpeechToText>();
builder.Services.AddSingleton<TranscriptionQueue>();
builder.Services.AddHostedService<TranscriptionWorker>();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

// Cloudflare Tunnel(cloudflared)이 같은 PC에서 http로 넘겨 주므로, 원래 https였다는 정보를 헤더에서 복원합니다.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    if (app.Services.GetRequiredService<IOptions<AppOptions>>().Value.SeedDemoData)
        await DemoSeeder.SeedAsync(db, app.Services.GetRequiredService<DataPaths>(), app.Services.GetRequiredService<ClinicClock>());
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPatientEndpoints();
app.MapVisitEndpoints();
app.MapCaptureEndpoints();
app.MapPhotoEndpoints();
app.MapNoteEndpoints();
app.MapDirectorEndpoints();
app.MapHub<VisitHub>(VisitHub.Path);
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// 화면: / (역할 선택) · /report (간호사 보고) · /director (원장 결과 확인) · /capture/{token} (폰 촬영)
app.MapFallbackToFile("/report", "report.html");
app.MapFallbackToFile("/director", "director.html");
app.MapFallbackToFile("/capture/{**rest}", "capture.html");

app.Run();

public partial class Program;
