using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using GoodMoNa.Server.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GoodMoNa.Server.Auth;

/// <summary>
/// 직무 이름. 02 인트라넷 서버의 StaffRole과 같아야 합니다(출입증의 role 값).
/// 직원 DB는 02에만 있고, 01은 출입증에 적힌 직무만 보고 권한을 나눕니다.
/// </summary>
public static class StaffRoles
{
    public const string Owner = "Owner";   // 대표원장
    public const string Doctor = "Doctor"; // 원장
    public const string Nurse = "Nurse";   // 간호사
    public const string Staff = "Staff";   // 직원
    public const string Admin = "Admin";   // 관리자
}

public static class Policies
{
    /// <summary>환자 검색·방문·촬영 QR·보고·사진 보기: 간호사·원장·대표원장</summary>
    public const string Clinical = "Clinical";

    /// <summary>원장 결과 확인·소견 녹음·녹음 삭제: 원장·대표원장</summary>
    public const string Director = "Director";

    /// <summary>이전 기록 가져오기: 대표원장·관리자</summary>
    public const string ImportAdmin = "ImportAdmin";

    public static readonly string[] ClinicalRoles = [StaffRoles.Owner, StaffRoles.Doctor, StaffRoles.Nurse];
    public static readonly string[] DirectorRoles = [StaffRoles.Owner, StaffRoles.Doctor];
    public static readonly string[] ImportAdminRoles = [StaffRoles.Owner, StaffRoles.Admin];
}

public static class StaffAuth
{
    public const string AccessCookie = "gm_access";

    /// <summary>
    /// 02가 발급한 출입증(gm_access 쿠키의 JWT)을 확인합니다. 서명 공개키는 02의 /.well-known/jwks.json에서 받아
    /// 캐시하고(키가 바뀌면 자동으로 다시 받음), 매 요청마다 02에 묻지 않습니다.
    /// </summary>
    public static void AddStaffAuth(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AppOptions>, TimeProvider>((o, app, time) =>
            {
                var auth = app.Value.Auth;
                o.Authority = auth.Authority;
                o.RequireHttpsMetadata = false; // 02는 같은 PC의 localhost (밖으로 나가지 않음)
                o.RefreshInterval = TimeSpan.FromSeconds(30); // 02가 늦게 켜져도 30초 뒤 다시 시도
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = auth.Issuer,
                    ValidAudience = auth.Audience,
                    NameClaimType = "name",
                    RoleClaimType = "role",
                    LifetimeValidator = (notBefore, expires, _, _) =>
                    {
                        var now = time.GetUtcNow().UtcDateTime;
                        var skew = TimeSpan.FromSeconds(30);
                        return (notBefore is null || notBefore.Value <= now + skew) && expires is not null && expires.Value > now - skew;
                    },
                };
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        if (string.IsNullOrEmpty(ctx.Token) && ctx.Request.Cookies.TryGetValue(AccessCookie, out var cookie))
                            ctx.Token = cookie;
                        return Task.CompletedTask;
                    },
                    OnChallenge = async ctx =>
                    {
                        ctx.HandleResponse();
                        await WriteProblem(ctx.Response, StatusCodes.Status401Unauthorized, "로그인이 필요합니다.");
                    },
                    OnForbidden = ctx => WriteProblem(ctx.Response, StatusCodes.Status403Forbidden, "이 기능을 쓸 권한이 없습니다. (직무 확인 필요)"),
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Clinical, p => p.RequireRole(Policies.ClinicalRoles))
            .AddPolicy(Policies.Director, p => p.RequireRole(Policies.DirectorRoles))
            .AddPolicy(Policies.ImportAdmin, p => p.RequireRole(Policies.ImportAdminRoles));
    }

    /// <summary>출입증의 직원번호(sub)와 이름. 진료 기록에 "누가 했는지"를 남길 때 씁니다.</summary>
    public static (int? Id, string Name) Who(ClaimsPrincipal user) =>
        (int.TryParse(user.FindFirstValue("sub"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null,
         user.Identity?.Name is { Length: > 0 } name ? name[..Math.Min(name.Length, 30)] : "알 수 없음");

    private static Task WriteProblem(HttpResponse response, int status, string detail)
    {
        response.StatusCode = status;
        response.ContentType = "application/problem+json; charset=utf-8";
        return response.WriteAsync(JsonSerializer.Serialize(new { status, title = status == 401 ? "인증 필요" : "권한 없음", detail }));
    }
}
