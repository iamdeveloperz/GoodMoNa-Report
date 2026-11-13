using System.Globalization;
using System.Text;

namespace GoodMoNa.Server.Contracts;

public static class PatientInput
{
    public const int MaxNameLength = 30;

    /// <summary>앞뒤 공백을 지우고 유니코드 NFC로 맞춥니다. (맥·iOS에서 자모가 분리돼 들어오는 경우 대비)</summary>
    public static string NormalizeName(string? name) =>
        (name ?? "").Trim().Normalize(NormalizationForm.FormC);

    /// <summary>YYYY-MM-DD, YYYY.MM.DD, YYYYMMDD, YYMMDD를 받습니다. 두 자리 연도는 올해보다 크면 1900년대로 봅니다.</summary>
    public static bool TryParseBirth(string? input, DateOnly today, out DateOnly birth)
    {
        birth = default;
        var digits = new string((input ?? "").Where(char.IsAsciiDigit).ToArray());

        if (digits.Length == 6)
        {
            var yy = int.Parse(digits[..2], CultureInfo.InvariantCulture);
            var century = yy > today.Year % 100 ? 1900 : 2000;
            digits = (century + yy).ToString(CultureInfo.InvariantCulture) + digits[2..];
        }

        if (digits.Length != 8 ||
            !DateOnly.TryParseExact(digits, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out birth))
            return false;

        return birth.Year >= 1900 && birth <= today;
    }

    /// <summary>김민수 → 김*수, 이서 → 이*</summary>
    public static string MaskName(string name) => name.Length switch
    {
        <= 1 => "*",
        2 => name[0] + "*",
        _ => name[0] + new string('*', name.Length - 2) + name[^1]
    };
}
