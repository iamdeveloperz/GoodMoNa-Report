using System.Text.RegularExpressions;

namespace GoodMoNa02.Server.Intranet;

/// <summary>자동차 번호판: "12가 3456", "123가-3456", "서울 12 가 3456" 같은 입력을 붙여 쓴 형태로 바꿔 저장합니다.</summary>
public static partial class Plates
{
    public static string Normalize(string? raw) => SpaceOrDash().Replace(raw ?? "", "");

    /// <summary>현행(12가3456 · 123가3456)과 지역 표기(서울12가3456)를 받습니다.</summary>
    public static bool IsValid(string normalized) => PlatePattern().IsMatch(normalized);

    /// <summary>검색어에서 숫자만 (이중 주차 연락: 뒤 4자리로 찾기)</summary>
    public static string Digits(string? raw) => new((raw ?? "").Where(char.IsAsciiDigit).ToArray());

    [GeneratedRegex(@"[\s\-]")]
    private static partial Regex SpaceOrDash();

    [GeneratedRegex(@"^([가-힣]{2})?\d{2,3}[가-힣]\d{4}$")]
    private static partial Regex PlatePattern();
}
