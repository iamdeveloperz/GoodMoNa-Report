using System.Net;
using GoodMoNa.Server.Infrastructure;
using Microsoft.Extensions.Options;

namespace GoodMoNa.Server.Auth;

/// <summary>
/// 화면(/report · /director · /import)을 열 때 로그인·직무를 먼저 확인합니다. API는 엔드포인트마다 RequireAuthorization이 막습니다.
/// 로그인 화면은 02 인트라넷 서버에 있습니다. 외부 주소(https://goodmona.hsdevs.com)에서는 Cloudflare가 /login을 02로 보내고,
/// 서버 PC에서 localhost:5001로 직접 연 경우에는 02 주소(Auth:Authority)로 보냅니다.
/// </summary>
public static class PageGate
{
    public static readonly IReadOnlyDictionary<string, string[]> Pages = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["/report"] = Policies.ClinicalRoles,
        ["/director"] = Policies.DirectorRoles,
        ["/import"] = Policies.ImportAdminRoles,
    };

    public static IApplicationBuilder UsePageGate(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var path = http.Request.Path.Value?.TrimEnd('/') ?? "";
            if (!HttpMethods.IsGet(http.Request.Method) || !Pages.TryGetValue(path, out var roles))
            {
                await next(http);
                return;
            }
            if (http.User.Identity?.IsAuthenticated != true)
            {
                http.Response.Redirect($"/login?next={Uri.EscapeDataString(http.Request.Path + http.Request.QueryString)}");
                return;
            }
            if (!roles.Any(http.User.IsInRole))
            {
                await WriteForbidden(http);
                return;
            }
            await next(http);
        });

    /// <summary>
    /// /login이 01까지 왔다 = Cloudflare를 거치지 않고 localhost로 직접 연 경우.
    /// 02의 로그인 화면으로 보내되, 로그인 뒤 돌아올 주소는 이 서버의 절대 주소로 바꿔 줍니다.
    /// </summary>
    public static IResult LocalLoginRedirect(HttpContext http, IOptions<AppOptions> options)
    {
        var next = http.Request.Query["next"].ToString();
        if (!next.StartsWith('/') || next.StartsWith("//", StringComparison.Ordinal)) next = "/";
        var back = $"{http.Request.Scheme}://{http.Request.Host}{next}";
        return Results.Redirect($"{options.Value.Auth.Authority.TrimEnd('/')}/login?next={Uri.EscapeDataString(back)}");
    }

    private static Task WriteForbidden(HttpContext http)
    {
        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        http.Response.ContentType = "text/html; charset=utf-8";
        var who = WebUtility.HtmlEncode($"{http.User.Identity?.Name} ({RoleLabel(http.User.FindFirst("role")?.Value)})");
        return http.Response.WriteAsync($$$"""
            <!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>권한 없음 — GoodMoNa</title>
            <style>body{margin:0;font-family:"Pretendard Variable",Pretendard,-apple-system,"Malgun Gothic",sans-serif;background:#fafaf7;color:#0e0e0e}
            main{max-width:640px;margin:15vh auto;padding:0 24px}h1{font-size:28px;letter-spacing:-.03em;border-bottom:1px solid #0e0e0e;padding-bottom:12px}
            p{line-height:1.7}a{color:inherit;font-weight:700}@media(prefers-color-scheme:dark){body{background:#111110;color:#f2f2ee}h1{border-color:#f2f2ee}}</style></head>
            <body><main><h1>이 화면을 쓸 권한이 없습니다</h1><p>{{{who}}} 계정으로 로그인되어 있습니다. 직무 변경이 필요하면 대표원장·관리자에게 요청해 주세요.</p>
            <p><a href="/intranet">인트라넷 홈</a> · <a href="/login?switch=1">다른 계정으로 로그인</a></p></main></body></html>
            """);
    }

    private static string RoleLabel(string? role) => role switch
    {
        StaffRoles.Owner => "대표원장",
        StaffRoles.Doctor => "원장",
        StaffRoles.Nurse => "간호사",
        StaffRoles.Staff => "직원",
        StaffRoles.Admin => "관리자",
        _ => "직무 없음",
    };
}
