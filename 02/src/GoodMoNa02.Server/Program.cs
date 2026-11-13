using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using GoodMoNa02.Server.Auth;
using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Endpoints;
using GoodMoNa02.Server.Infrastructure;
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
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UsePageGate(new Dictionary<string, string[]?>(StringComparer.OrdinalIgnoreCase)
{
    ["/intranet"] = null,
    ["/intranet/admin"] = Policies.ManageStaffRoles,
});
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/info", (ClinicClock clock) => Results.Ok(new { project = "GoodMoNa 02", today = clock.Today }));
app.MapAuthEndpoints();
app.MapAdminEndpoints();

// 화면: /login · /signup · /intranet (홈) · /intranet/admin (직원 승인). 정적 파일은 /intranet-assets/ 아래 (01과 겹치지 않게)
app.MapGet("/", () => Results.Redirect("/intranet"));
app.MapFallbackToFile("/login", "login.html");
app.MapFallbackToFile("/signup", "signup.html");
app.MapFallbackToFile("/intranet", "intranet.html");
app.MapFallbackToFile("/intranet/admin", "intranet-admin.html");

app.Run();

public partial class Program;
