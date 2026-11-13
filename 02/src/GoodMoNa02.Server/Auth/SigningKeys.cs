using System.Security.Cryptography;
using GoodMoNa02.Server.Infrastructure;
using Microsoft.IdentityModel.Tokens;

namespace GoodMoNa02.Server.Auth;

/// <summary>
/// 출입증 서명용 RSA 키. 처음 실행할 때 데이터 폴더(keys/signing-key.pem)에 만들고 이후 계속 씁니다.
/// 개인키는 이 서버만 가지고, 01 진료 서버는 /.well-known/jwks.json의 공개키로 서명을 확인만 합니다.
/// 키 파일을 지우면 새 키가 만들어지고 모든 직원이 다시 로그인해야 합니다.
/// </summary>
public sealed class SigningKeys
{
    public SigningKeys(DataPaths paths)
    {
        var file = paths.SigningKeyFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        var rsa = RSA.Create();
        if (File.Exists(file))
        {
            rsa.ImportFromPem(File.ReadAllText(file));
        }
        else
        {
            rsa.KeySize = 2048;
            var temp = file + ".part";
            File.WriteAllText(temp, rsa.ExportPkcs8PrivateKeyPem());
            File.Move(temp, file, overwrite: true);
        }

        var publicParams = rsa.ExportParameters(includePrivateParameters: false);
        KeyId = Base64UrlEncoder.Encode(SHA256.HashData(publicParams.Modulus!))[..16];
        PrivateKey = new RsaSecurityKey(rsa) { KeyId = KeyId };
        PublicKey = new RsaSecurityKey(publicParams) { KeyId = KeyId };
    }

    public string KeyId { get; }
    public RsaSecurityKey PrivateKey { get; }
    public RsaSecurityKey PublicKey { get; }

    public JsonWebKey PublicJwk
    {
        get
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(PublicKey);
            jwk.Use = "sig";
            jwk.Alg = SecurityAlgorithms.RsaSha256;
            return jwk;
        }
    }
}
