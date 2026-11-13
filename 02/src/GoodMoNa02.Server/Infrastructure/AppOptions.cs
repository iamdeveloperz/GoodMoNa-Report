namespace GoodMoNa02.Server.Infrastructure;

public sealed class AppOptions
{
    public const string Section = "App";

    /// <summary>SQLite·서명 키를 둘 폴더. 비우면 %LOCALAPPDATA%\GoodMoNa02. OneDrive 동기화 폴더는 피하세요.</summary>
    public string? DataRoot { get; set; }

    public string TimeZone { get; set; } = "Asia/Seoul";

    public AuthOptions Auth { get; set; } = new();
}

/// <summary>로그인·출입증(토큰) 설정. Issuer·Audience는 01 진료 서버 설정과 같아야 합니다.</summary>
public sealed class AuthOptions
{
    public string Issuer { get; set; } = "goodmona-intranet";
    public string Audience { get; set; } = "goodmona";

    /// <summary>출입증 유효 시간. 퇴사·직무 변경이 다른 서버에 반영되기까지 걸리는 최대 시간이기도 합니다.</summary>
    public int AccessMinutes { get; set; } = 15;

    /// <summary>로그인 유지 기간 (마지막 사용부터)</summary>
    public int RefreshDays { get; set; } = 14;

    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 10;

    /// <summary>IP 하나가 1분에 보낼 수 있는 로그인·가입 요청 수</summary>
    public int AuthRequestsPerMinute { get; set; } = 30;
}

public sealed class DataPaths
{
    public DataPaths(AppOptions options)
    {
        Root = string.IsNullOrWhiteSpace(options.DataRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoodMoNa02")
            : Path.GetFullPath(options.DataRoot);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "goodmona02.db");
    public string ConnectionString => $"Data Source={DatabaseFile}";

    /// <summary>출입증 서명용 개인키. 데이터 폴더 안에만 두고 절대 리포에 넣지 않습니다.</summary>
    public string SigningKeyFile => Path.Combine(Root, "keys", "signing-key.pem");

    /// <summary>상대 경로를 데이터 폴더 안의 절대 경로로 바꿉니다. 폴더 밖을 가리키면 거부합니다.</summary>
    public string Resolve(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("데이터 폴더 밖의 경로입니다.");
        return full;
    }
}
