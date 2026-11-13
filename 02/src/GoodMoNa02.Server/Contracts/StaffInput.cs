using System.Text;
using System.Text.RegularExpressions;
using GoodMoNa02.Server.Data;

namespace GoodMoNa02.Server.Contracts;

/// <summary>가입·로그인 입력값 정리와 검사. 오류 문장은 화면에 그대로 띄웁니다.</summary>
public static partial class StaffInput
{
    public const int MaxNameLength = 30;
    public const int MinPasswordLength = 8;

    /// <summary>앞뒤 공백 제거 + 소문자. 대소문자만 다른 아이디가 두 개 생기지 않게 합니다.</summary>
    public static string NormalizeLoginId(string? loginId) => (loginId ?? "").Trim().ToLowerInvariant();

    public static string NormalizeName(string? name) => (name ?? "").Trim().Normalize(NormalizationForm.FormC);

    public static string? CheckLoginId(string loginId) =>
        LoginIdPattern().IsMatch(loginId) ? null : "아이디는 영문 소문자·숫자로 시작하는 3~30자(영문·숫자·. _ -)로 정해 주세요.";

    public static string? CheckName(string name) =>
        name.Length is > 0 and <= MaxNameLength ? null : $"이름을 1~{MaxNameLength}자로 입력해 주세요.";

    public static string? CheckPassword(string? password, string loginId)
    {
        if (password is null || password.Length < MinPasswordLength) return $"비밀번호는 {MinPasswordLength}자 이상이어야 합니다.";
        if (password.Length > 128) return "비밀번호는 128자까지 쓸 수 있습니다.";
        if (loginId.Length > 0 && password.Contains(loginId, StringComparison.OrdinalIgnoreCase)) return "비밀번호에 아이디를 넣을 수 없습니다.";
        return null;
    }

    public static bool TryParseRole(string? value, out StaffRole role) =>
        Enum.TryParse(value, ignoreCase: true, out role) && Enum.IsDefined(role);

    public static string? Optional(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,29}$")]
    private static partial Regex LoginIdPattern();
}
