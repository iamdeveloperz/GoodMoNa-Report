using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace GoodMoNa.Server.Infrastructure;

public static class CaptureTokens
{
    /// <summary>256비트 난수를 URL에 그대로 넣을 수 있는 43자 문자열로 만듭니다.</summary>
    public static string Create() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool IsWellFormed(string token) =>
        token.Length == 43 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
