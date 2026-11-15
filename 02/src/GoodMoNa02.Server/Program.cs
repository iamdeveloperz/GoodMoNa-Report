using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using GoodMoNa02.Server.Auth;
using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Endpoints;
using GoodMoNa02.Server.Infrastructure;
using GoodMoNa02.Server.Intranet;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// GoodMoNa 2번 과제: 사내 인트라넷 서버.
// 직원 계정(가입 → 승인)과 로그인을 맡고, 출입증(JWT)을 발급합니다. 01 진료 서버는 이 출입증으로 간호사·원장을 구분합니다.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "GoodMoNa02");

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.Section));
builder.Services.AddSingleton(sp => new DataPaths(sp.GetRequiredService<IOptions<AppOptions>>().Value));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ClinicClock>();
builder.Services.AddDbContext<AppDbContext>((sp, o) => o.UseSqlite(sp.GetRequiredService<DataPaths>().ConnectionString));

builder.Services.AddSingleton<IPasswordHasher<Staff>, PasswordHasher<Staff>>();
builder.Services.AddStaffAuth();

// 로그인·가입 요청은 IP마다 1분에 N번까지 (비밀번호 대입·가입 도배 방지)
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.RateLimitPolicy, http =>
    {
        var perMinute = http.RequestServices.GetRequiredService<IOptions<AppOptions>>().Value.Auth.AuthRequestsPerMinute;
        var ip = http.Request.Headers["Cf-Connecting-IP"].FirstOrDefault() ?? http.Connection.RemoteIpAddress?.ToString() ?? "local";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) });
    });
});

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

// Cloudflare Tunnel이 같은 PC에서 http로 넘겨 주므로, 원래 https였다는 정보를 헤더에서 복원합니다.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await ClinicSetup.EnsureAsync(db); // 병원 설정 한 줄 + 내장 공휴일
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UsePageGate(new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase)
{
    ["/intranet"] = null,
    ["/intranet/admin"] = Policies.ManageStaffRoles,
    ["/intranet/settings"] = Policies.ManageStaffRoles,
    ["/intranet/attendance/kiosk"] = Policies.ManageStaffRoles,
    ["/intranet/notices"] = null,
    ["/intranet/leave"] = null,
    ["/intranet/attendance"] = null,
    ["/intranet/parking"] = null,
    ["/intranet/people"] = null,
});
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/info", (ClinicClock clock) => Results.Ok(new { project = "GoodMoNa 02", today = clock.Today }));
app.MapAuthEndpoints();
app.MapAdminEndpoints();

// 인트라넷 업무 API는 모두 /api/intranet 아래 (Cloudflare 터널이 이 경로를 02로 보냄). 로그인한 직원이면 쓰고, 관리 기능은 엔드포인트마다 정책.
var intranet = app.MapGroup("/api/intranet").RequireAuthorization();
intranet.MapHomeEndpoints();
intranet.MapNoticeEndpoints();
intranet.MapLeaveEndpoints();
intranet.MapAttendanceEndpoints();
intranet.MapParkingEndpoints();
intranet.MapSettingsEndpoints();

// 화면: /login · /signup · /intranet (홈) · 공지 · 연차 · 출퇴근(+입구 QR) · 주차 · 주소록 · 직원 관리 · 설정.
// 정적 파일은 /intranet-assets/ 아래 (01과 겹치지 않게)
app.MapGet("/", () => Results.Redirect("/intranet"));
app.MapFallbackToFile("/login", "login.html");
app.MapFallbackToFile("/signup", "signup.html");
app.MapFallbackToFile("/intranet", "intranet.html");
app.MapFallbackToFile("/intranet/admin", "intranet-admin.html");
app.MapFallbackToFile("/intranet/notices", "notices.html");
app.MapFallbackToFile("/intranet/leave", "leave.html");
app.MapFallbackToFile("/intranet/attendance", "attendance.html");
app.MapFallbackToFile("/intranet/attendance/kiosk", "kiosk.html");
app.MapFallbackToFile("/intranet/parking", "parking.html");
app.MapFallbackToFile("/intranet/people", "people.html");
app.MapFallbackToFile("/intranet/settings", "settings.html");

app.Run();

public partial class Program;
