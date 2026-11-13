namespace GoodMoNa02.Server.Endpoints;

/// <summary>오류 응답은 모두 ProblemDetails이며, 화면에 그대로 띄울 수 있는 한국어 문장을 detail에 담습니다. (01과 같은 규칙)</summary>
internal static class Problems
{
    public static IResult Validation(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }, detail: message, title: "입력값 오류");

    public static IResult NotFound(string message) =>
        Results.Problem(detail: message, statusCode: StatusCodes.Status404NotFound, title: "없음");

    public static IResult Conflict(string message) =>
        Results.Problem(detail: message, statusCode: StatusCodes.Status409Conflict, title: "상태 충돌");
}
