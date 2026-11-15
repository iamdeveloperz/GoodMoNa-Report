using System.Security.Cryptography;
using System.Text;

namespace GoodMoNa02.Server.Intranet;

/// <summary>
/// 병원 입구 화면(키오스크)에 띄우는 출근 QR 코드. 30초마다 바뀌는 코드(TOTP와 같은 방식, HMAC-SHA256)라서
/// 사진으로 찍어 집에서 쓰거나 남에게 보내도 1분 안에 쓸모가 없어집니다.
/// </summary>
public static class AttendanceCodes
{
    public const int StepSeconds = 30;

    public static long StepOf(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds() / StepSeconds;

    public static string CodeFor(string secretHex, long step)
    {
        var mac = HMACSHA256.HashData(Convert.FromHexString(secretHex), Encoding.ASCII.GetBytes(step.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return Convert.ToBase64String(mac, 0, 9).Replace('+', '-').Replace('/', '_'); // 12자
    }

    public static (string Code, DateTime ExpiresAt) Current(string secretHex, DateTime utc)
    {
        var step = StepOf(utc);
        return (CodeFor(secretHex, step), DateTimeOffset.FromUnixTimeSeconds((step + 1) * StepSeconds).UtcDateTime);
    }

    /// <summary>지금 코드 또는 바로 앞 코드(찍는 사이에 바뀐 경우)면 통과</summary>
    public static bool IsValid(string secretHex, string? code, DateTime utc)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 12) return false;
        var step = StepOf(utc);
        var given = Encoding.ASCII.GetBytes(code);
        return new[] { step, step - 1 }.Any(s => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(CodeFor(secretHex, s)), given));
    }
}
