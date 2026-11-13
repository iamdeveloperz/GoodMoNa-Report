using System.Text.Json;
using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GoodMoNa02.Server.Auth;

public static class Policies
{
    /// <summary>직원 승인·직무 변경</summary>
    public const string ManageStaff = "ManageStaff";

    public static readonly string[] ManageStaffRoles = [nameof(StaffRole.Owner), nameof(StaffRole.Admin)];
}

public static class AuthSetup
{
    /// <summary>
    /// 이 서버가 만든 출입증을 이 서버에서도 확인합니다(내 정보·관리 화면).
    /// 브라우저는 Authorization 헤더 대신 gm_access 쿠키로 보내므로 쿠키에서 꺼냅니다.
    /// 만료 판단은 TimeProvider 기준이라 테스트에서 시계를 바꿔 끼울 수 있습니다.
    /// </summary>
    public static void AddStaffAuth(this IServiceCollection services)
    {
        services.AddSingleton<SigningKeys>();
        services.AddSingleton<TokenService>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<SigningKeys, TimeProvider, IOptions<AppOptions>>((o, keys, time, app) =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = app.Value.Auth.Issuer,
                    ValidAudience = app.Value.Auth.Audience,
                    IssuerSigningKey = keys.PublicKey,
                    NameClaimType = "name",
                    RoleClaimType = "role",
                    LifetimeValidator = (notBefore, expires, _, _) => IsWithin(time, notBefore, expires),
                };
                o.Events = KoreanEvents();
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.ManageStaff, p => p.RequireRole(Policies.ManageStaffRoles));
    }

    public static bool IsWithin(TimeProvider time, DateTime? notBefore, DateTime? expires)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var skew = TimeSpan.FromSeconds(30);
        return (notBefore is null || notBefore.Value <= now + skew) && expires is not null && expires.Value > now - skew;
    }

    /// <summary>401·403도 다른 오류처럼 한국어 ProblemDetails로 돌려줍니다.</summary>
    public static JwtBearerEvents KoreanEvents() => new()
    {
        OnMessageReceived = ctx =>
        {
            if (string.IsNullOrEmpty(ctx.Token) && ctx.Request.Cookies.TryGetValue(TokenService.AccessCookie, out var cookie))
                ctx.Token = cookie;
            return Task.CompletedTask;
        },
        OnChallenge = async ctx =>
        {
            ctx.HandleResponse();
            await WriteProblem(ctx.Response, StatusCodes.Status401Unauthorized, "로그인이 필요합니다.");
        },
        OnForbidden = ctx => WriteProblem(ctx.Response, StatusCodes.Status403Forbidden, "이 기능을 쓸 권한이 없습니다."),
    };

    private static Task WriteProblem(HttpResponse response, int status, string detail)
    {
        response.StatusCode = status;
        response.ContentType = "application/problem+json; charset=utf-8";
        return response.WriteAsync(JsonSerializer.Serialize(new { status, title = status == 401 ? "인증 필요" : "권한 없음", detail }));
    }
}
