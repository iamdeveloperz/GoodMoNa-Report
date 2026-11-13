using System.Security.Cryptography;
using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace GoodMoNa02.Server.Auth;

/// <summary>
/// 출입증(접근 토큰, JWT RS256)과 로그인 유지용 갱신 토큰을 만듭니다.
/// 출입증 안에는 직원번호(sub)·이름·직무(role)만 넣습니다. 비밀번호·연락처 같은 건 넣지 않습니다.
/// </summary>
public sealed class TokenService(SigningKeys keys, IOptions<AppOptions> options, ClinicClock clock)
{
    public const string AccessCookie = "gm_access";
    public const string RefreshCookie = "gm_refresh";

    private readonly AuthOptions _auth = options.Value.Auth;
    private readonly JsonWebTokenHandler _handler = new();

    public (string Token, DateTime ExpiresAt) CreateAccessToken(Staff staff)
    {
        var now = clock.UtcNow;
        var expires = now.AddMinutes(_auth.AccessMinutes);
        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _auth.Issuer,
            Audience = _auth.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(keys.PrivateKey, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = staff.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["name"] = staff.Name,
                ["login"] = staff.LoginId,
                ["role"] = staff.Role.ToString(),
            },
        });
        return (token, expires);
    }

    /// <summary>원문(쿠키에 넣을 값)과 DB에 저장할 해시</summary>
    public static (string Token, string Hash) NewRefreshToken()
    {
        var token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        return (token, Hash(token));
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    public DateTime RefreshExpiry => clock.UtcNow.AddDays(_auth.RefreshDays);

    /// <summary>
    /// 출입증은 경로 전체(/)에 보내서 01 진료 서버도 받게 하고, 갱신 토큰은 /api/auth에만 보내 02만 받게 합니다.
    /// SameSite=Lax로 다른 사이트에서 보낸 POST에는 쿠키가 붙지 않습니다(CSRF 방지).
    /// 출입증 쿠키에는 만료일을 두지 않습니다: 실제 만료는 토큰 안의 exp로 판단합니다.
    /// </summary>
    public void WriteCookies(HttpContext http, string access, string refresh, DateTime refreshExpires)
    {
        var secure = http.Request.IsHttps;
        http.Response.Cookies.Append(AccessCookie, access, new CookieOptions
        {
            HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true,
        });
        http.Response.Cookies.Append(RefreshCookie, refresh, new CookieOptions
        {
            HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Strict, Path = "/api/auth", Expires = refreshExpires, IsEssential = true,
        });
    }

    public static void ClearCookies(HttpContext http)
    {
        http.Response.Cookies.Delete(AccessCookie, new CookieOptions { Path = "/" });
        http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = "/api/auth" });
    }
}
