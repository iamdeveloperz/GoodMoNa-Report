namespace GoodMoNa.Server.Endpoints;

/// <summary>오류 응답은 모두 ProblemDetails이며, 화면에 그대로 띄울 수 있는 한국어 문장을 detail에 담습니다.</summary>
internal static class Problems
{
    public static IResult Validation(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }, detail: message, title: "입력값 오류");

    public static IResult Forbidden(string message) =>
        Results.Problem(detail: message, statusCode: StatusCodes.Status403Forbidden, title: "권한 없음");

    public static IResult NotFound(string message) =>
        Results.Problem(detail: message, statusCode: StatusCodes.Status404NotFound, title: "없음");

    public static IResult Conflict(string message, object? existing = null) =>
        Results.Problem(detail: message, statusCode: StatusCodes.Status409Conflict, title: "상태 충돌",
            extensions: existing is null ? null : new Dictionary<string, object?> { ["existing"] = existing });

    public static IResult Gone(string message) =>
        Results.Problem(detail: message, statusCode: StatusCodes.Status410Gone, title: "만료됨");

    public static IResult TooLarge(string message) =>
        Results.Problem(detail: message, statusCode: StatusCodes.Status413PayloadTooLarge, title: "파일이 너무 큼");

    public static IResult Unsupported(string message) =>
        Results.Problem(detail: message, statusCode: StatusCodes.Status415UnsupportedMediaType, title: "지원하지 않는 형식");
}
