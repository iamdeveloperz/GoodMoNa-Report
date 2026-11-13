using System.Net;
using System.Security.Claims;
using GoodMoNa02.Server.Auth;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoodMoNa02.Server.Endpoints;

/// <summary>
/// 회원가입(승인 필수) · 로그인 · 출입증 갱신 · 로그아웃 · 내 정보, 그리고 01이 서명 확인에 쓰는 공개키.
/// 가입하면 승인 대기(Pending)로 들어가고, 대표원장·관리자가 승인해야 로그인할 수 있습니다.
/// 아직 관리자가 한 명도 없을 때만, 서버 PC에서 첫 관리자를 바로 만들 수 있습니다(bootstrap).
/// </summary>
public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";

    /// <summary>갱신 토큰을 두 탭이 동시에 쓰면 한쪽은 이미 바뀐 토큰을 보냅니다. 이 시간 안이면 도난으로 보지 않습니다.</summary>
    private static readonly TimeSpan ReuseGrace = TimeSpan.FromSeconds(30);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth");
        g.MapPost("/signup", Signup).RequireRateLimiting(RateLimitPolicy);
        g.MapGet("/bootstrap", (HttpContext http, AppDbContext db, CancellationToken ct) => BootstrapStatus(http, db, ct));
        g.MapPost("/bootstrap", Bootstrap).RequireRateLimiting(RateLimitPolicy);
        g.MapPost("/login", Login).RequireRateLimiting(RateLimitPolicy);
        g.MapPost("/refresh", Refresh);
        g.MapPost("/logout", Logout);
        g.MapGet("/me", Me).RequireAuthorization();

        // 01 진료 서버(JwtBearer)가 처음 한 번 읽어 가는 발급자 정보와 공개키
        app.MapGet("/.well-known/openid-configuration", (HttpRequest req, IOptions<AppOptions> o) =>
        {
            var baseUrl = $"{req.Scheme}://{req.Host}";
            return Results.Json(new Dictionary<string, object>
            {
                ["issuer"] = o.Value.Auth.Issuer,
                ["jwks_uri"] = $"{baseUrl}/.well-known/jwks.json",
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            });
        });
        app.MapGet("/.well-known/jwks.json", (SigningKeys keys) =>
            Results.Json(new { keys = new[] { keys.PublicJwk } }, new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
    }

    /* ---------- 가입 ---------- */

    private static async Task<IResult> Signup(SignupRequest req, AppDbContext db, IPasswordHasher<Staff> hasher, ClinicClock clock, CancellationToken ct)
    {
        var loginId = StaffInput.NormalizeLoginId(req.LoginId);
        var name = StaffInput.NormalizeName(req.Name);
        if (StaffInput.CheckName(name) is { } e1) return Problems.Validation("name", e1);
        if (StaffInput.CheckLoginId(loginId) is { } e2) return Problems.Validation("loginId", e2);
        if (StaffInput.CheckPassword(req.Password, loginId) is { } e3) return Problems.Validation("password", e3);
        if (!StaffInput.TryParseRole(req.RequestedRole, out var role)) return Problems.Validation("requestedRole", "직무를 골라 주세요.");
        if (await db.Staff.AnyAsync(s => s.LoginId == loginId, ct)) return Problems.Conflict("이미 쓰는 아이디입니다. 다른 아이디를 정해 주세요.");

        var staff = new Staff
        {
            LoginId = loginId,
            Name = name,
            PasswordHash = "",
            RequestedRole = role,
            Role = role,
            Status = StaffStatus.Pending,
            Department = StaffInput.Optional(req.Department, 30),
            Phone = StaffInput.Optional(req.Phone, 20),
            CreatedAt = clock.UtcNow,
        };
        staff.PasswordHash = hasher.HashPassword(staff, req.Password!);
        db.Staff.Add(staff);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Problems.Conflict("이미 쓰는 아이디입니다. 다른 아이디를 정해 주세요.");
        }
        return Results.Created($"/api/admin/staff/{staff.Id}", new SignupResultDto(loginId, StaffStatus.Pending));
    }

    /* ---------- 첫 관리자 ---------- */

    /// <summary>Cloudflare를 거치지 않은 요청(서버 PC에서 localhost로 연 경우)인지</summary>
    public static bool IsLocal(HttpContext http) =>
        !http.Request.Headers.ContainsKey("Cf-Connecting-IP") &&
        (http.Connection.RemoteIpAddress is null || IPAddress.IsLoopback(http.Connection.RemoteIpAddress));

    private static async Task<bool> CanBootstrap(HttpContext http, AppDbContext db, CancellationToken ct) =>
        IsLocal(http) && !await db.Staff.AnyAsync(s => s.Status == StaffStatus.Active && (s.Role == StaffRole.Owner || s.Role == StaffRole.Admin), ct);

    private static async Task<IResult> BootstrapStatus(HttpContext http, AppDbContext db, CancellationToken ct) =>
        Results.Ok(new BootstrapStatusDto(await CanBootstrap(http, db, ct)));

    private static async Task<IResult> Bootstrap(
        BootstrapRequest req, HttpContext http, AppDbContext db, IPasswordHasher<Staff> hasher, TokenService tokens, ClinicClock clock, CancellationToken ct)
    {
        if (!await CanBootstrap(http, db, ct))
            return Problems.Forbidden("첫 관리자는 관리자가 한 명도 없을 때, 서버 PC에서만 만들 수 있습니다. 가입 신청 후 승인을 받아 주세요.");

        var loginId = StaffInput.NormalizeLoginId(req.LoginId);
        var name = StaffInput.NormalizeName(req.Name);
        if (StaffInput.CheckName(name) is { } e1) return Problems.Validation("name", e1);
        if (StaffInput.CheckLoginId(loginId) is { } e2) return Problems.Validation("loginId", e2);
        if (StaffInput.CheckPassword(req.Password, loginId) is { } e3) return Problems.Validation("password", e3);
        var role = StaffRole.Owner;
        if (req.Role is not null && (!StaffInput.TryParseRole(req.Role, out role) || role is not (StaffRole.Owner or StaffRole.Admin)))
            return Problems.Validation("role", "첫 관리자는 대표원장 또는 관리자여야 합니다.");
        if (await db.Staff.AnyAsync(s => s.LoginId == loginId, ct)) return Problems.Conflict("이미 쓰는 아이디입니다.");

        var now = clock.UtcNow;
        var staff = new Staff
        {
            LoginId = loginId, Name = name, PasswordHash = "", Role = role, RequestedRole = role,
            Status = StaffStatus.Active, CreatedAt = now, DecidedAt = now,
        };
        staff.PasswordHash = hasher.HashPassword(staff, req.Password!);
        db.Staff.Add(staff);
        await db.SaveChangesAsync(ct);

        await IssueAsync(http, db, tokens, staff, now, ct);
        return Results.Created("/api/auth/me", ToMe(staff));
    }

    /* ---------- 로그인 ---------- */

    // 없는 아이디여도 비밀번호 검사 시간을 똑같이 써서, 응답 시간으로 아이디 존재 여부를 알 수 없게 합니다.
    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<Staff>().HashPassword(null!, "dummy-password-for-timing"));

    private static async Task<IResult> Login(
        LoginRequest req, HttpContext http, AppDbContext db, IPasswordHasher<Staff> hasher, TokenService tokens,
        ClinicClock clock, IOptions<AppOptions> options, CancellationToken ct)
    {
        const string invalid = "아이디 또는 비밀번호가 맞지 않습니다.";
        var auth = options.Value.Auth;
        var loginId = StaffInput.NormalizeLoginId(req.LoginId);
        var password = req.Password ?? "";
        var now = clock.UtcNow;

        var staff = await db.Staff.FirstOrDefaultAsync(s => s.LoginId == loginId, ct);
        if (staff is null)
        {
            hasher.VerifyHashedPassword(null!, DummyHash.Value, password);
            return Problems.Unauthorized(invalid);
        }

        if (staff.LockedUntil > now)
            return Problems.Locked($"비밀번호를 여러 번 틀려 잠시 잠겼습니다. {(int)Math.Ceiling((staff.LockedUntil.Value - now).TotalMinutes)}분 뒤 다시 시도해 주세요.");

        var verified = hasher.VerifyHashedPassword(staff, staff.PasswordHash, password);
        if (verified == PasswordVerificationResult.Failed)
        {
            staff.FailedLogins++;
            var locked = staff.FailedLogins >= auth.MaxFailedLogins;
            if (locked)
            {
                staff.LockedUntil = now.AddMinutes(auth.LockoutMinutes);
                staff.FailedLogins = 0;
            }
            await db.SaveChangesAsync(ct);
            return locked
                ? Problems.Locked($"비밀번호를 {auth.MaxFailedLogins}번 틀려 {auth.LockoutMinutes}분 동안 잠겼습니다.")
                : Problems.Unauthorized(invalid);
        }

        if (verified == PasswordVerificationResult.SuccessRehashNeeded) staff.PasswordHash = hasher.HashPassword(staff, password);
        staff.FailedLogins = 0;
        staff.LockedUntil = null;
        await db.SaveChangesAsync(ct);

        // 비밀번호가 맞은 뒤에만 계정 상태를 알려 줍니다 (남의 아이디로 상태를 떠볼 수 없게).
        switch (staff.Status)
        {
            case StaffStatus.Pending:
                return Problems.Forbidden("가입 승인 대기 중입니다. 대표원장 또는 관리자가 승인하면 로그인할 수 있습니다.", "pending");
            case StaffStatus.Rejected:
                return Problems.Forbidden($"가입이 반려되었습니다.{(staff.RejectReason is { Length: > 0 } r ? $" 사유: {r}" : "")}", "rejected");
            case StaffStatus.Disabled:
                return Problems.Forbidden("사용이 중지된 계정입니다. 관리자에게 문의해 주세요.", "disabled");
        }

        await IssueAsync(http, db, tokens, staff, now, ct);
        return Results.Ok(ToMe(staff));
    }

    /* ---------- 갱신 · 로그아웃 ---------- */

    /// <summary>
    /// 출입증이 만료되면 화면이 이걸 불러 새 출입증을 받습니다. 이때 DB에서 재직 여부·현재 직무를 다시 확인하므로
    /// 퇴사 처리·직무 변경은 늦어도 출입증 유효 시간(15분) 안에 01·02 모두에 반영됩니다.
    /// </summary>
    private static async Task<IResult> Refresh(HttpContext http, AppDbContext db, TokenService tokens, ClinicClock clock, CancellationToken ct)
    {
        const string again = "로그인이 필요합니다.";
        if (!http.Request.Cookies.TryGetValue(TokenService.RefreshCookie, out var raw) || string.IsNullOrEmpty(raw))
            return Problems.Unauthorized(again);

        var now = clock.UtcNow;
        var hash = TokenService.Hash(raw);
        var session = await db.StaffSessions.Include(s => s.Staff).FirstOrDefaultAsync(s => s.TokenHash == hash, ct);
        if (session is null || session.ExpiresAt <= now)
        {
            TokenService.ClearCookies(http);
            return Problems.Unauthorized(again);
        }

        var staff = session.Staff;
        if (staff.Status != StaffStatus.Active)
        {
            await RevokeAllAsync(db, staff.Id, now, ct);
            TokenService.ClearCookies(http);
            return Problems.Unauthorized("사용할 수 없는 계정입니다. 다시 로그인해 주세요.");
        }

        if (session.RevokedAt is not null)
        {
            // 방금 갱신으로 바뀐 토큰이고, 그 뒤를 이은 세션이 아직 살아 있을 때만 정상으로 봅니다.
            if (session.ReplacedAt is { } replacedAt && now - replacedAt <= ReuseGrace &&
                await db.StaffSessions.AnyAsync(s => s.StaffId == staff.Id && s.RevokedAt == null && s.CreatedAt >= replacedAt, ct))
            {
                // 다른 탭이 방금 갱신한 경우: 새 갱신 토큰은 그 탭이 이미 받았으므로 출입증만 새로 줍니다.
                var (access, _) = tokens.CreateAccessToken(staff);
                http.Response.Cookies.Append(TokenService.AccessCookie, access, new CookieOptions
                {
                    HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true,
                });
                return Results.Ok(ToMe(staff));
            }

            // 이미 바뀐 토큰이 한참 뒤에 다시 쓰임 = 토큰이 새어 나갔을 수 있음 → 그 직원의 모든 세션을 끊습니다.
            await RevokeAllAsync(db, staff.Id, now, ct);
            TokenService.ClearCookies(http);
            return Problems.Unauthorized("보안을 위해 모든 기기에서 로그아웃했습니다. 다시 로그인해 주세요.");
        }

        session.RevokedAt = now;
        session.ReplacedAt = now;
        await IssueAsync(http, db, tokens, staff, now, ct);
        return Results.Ok(ToMe(staff));
    }

    private static async Task<IResult> Logout(HttpContext http, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        if (http.Request.Cookies.TryGetValue(TokenService.RefreshCookie, out var raw) && !string.IsNullOrEmpty(raw))
        {
            var hash = TokenService.Hash(raw);
            await db.StaffSessions.Where(s => s.TokenHash == hash && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, clock.UtcNow), ct);
        }
        TokenService.ClearCookies(http);
        return Results.NoContent();
    }

    private static async Task<IResult> Me(ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var staff = await db.Staff.AsNoTracking().FirstOrDefaultAsync(s => s.Id == StaffId(user), ct);
        return staff is { Status: StaffStatus.Active } ? Results.Ok(ToMe(staff)) : Problems.Unauthorized("사용할 수 없는 계정입니다.");
    }

    /* ---------- 도우미 ---------- */

    public static int StaffId(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue("sub"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0;

    public static MeDto ToMe(Staff s) => new(s.Id, s.LoginId, s.Name, s.Role);

    private static async Task IssueAsync(HttpContext http, AppDbContext db, TokenService tokens, Staff staff, DateTime now, CancellationToken ct)
    {
        var (refresh, hash) = TokenService.NewRefreshToken();
        var expires = tokens.RefreshExpiry;
        db.StaffSessions.Add(new StaffSession { StaffId = staff.Id, TokenHash = hash, CreatedAt = now, ExpiresAt = expires });
        await db.SaveChangesAsync(ct);
        var (access, _) = tokens.CreateAccessToken(staff);
        tokens.WriteCookies(http, access, refresh, expires);
    }

    public static Task RevokeAllAsync(AppDbContext db, int staffId, DateTime now, CancellationToken ct) =>
        db.StaffSessions.Where(s => s.StaffId == staffId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
}
