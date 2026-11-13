using System.Net;

namespace GoodMoNa02.Server.Auth;

/// <summary>
/// 화면 주소를 열 때 로그인·직무를 먼저 확인합니다. (API는 각 엔드포인트의 RequireAuthorization이 막음)
/// 로그인이 안 됐으면 /login?next=… 로 보내고, 직무가 안 맞으면 안내 화면(403)을 보여 줍니다.
/// 01 진료 서버에도 같은 규칙의 PageGate가 있습니다.
/// </summary>
public static class PageGate
{
    /// <param name="pages">화면 경로 → 허용 직무 (null이면 로그인만 하면 됨)</param>
    public static IApplicationBuilder UsePageGate(this IApplicationBuilder app, IReadOnlyDictionary<string, string[]?> pages) =>
        app.Use(async (http, next) =>
        {
            var path = http.Request.Path.Value?.TrimEnd('/') ?? "";
            if (!HttpMethods.IsGet(http.Request.Method) || !pages.TryGetValue(path, out var roles))
            {
                await next(http);
                return;
            }

            if (http.User.Identity?.IsAuthenticated != true)
            {
                http.Response.Redirect($"/login?next={Uri.EscapeDataString(http.Request.Path + http.Request.QueryString)}");
                return;
            }
            if (roles is not null && !roles.Any(http.User.IsInRole))
            {
                await WriteForbidden(http);
                return;
            }
            await next(http);
        });

    public static Task WriteForbidden(HttpContext http)
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

    public static string RoleLabel(string? role) => role switch
    {
        "Owner" => "대표원장",
        "Doctor" => "원장",
        "Nurse" => "간호사",
        "Staff" => "직원",
        "Admin" => "관리자",
        _ => "직무 없음",
    };
}
