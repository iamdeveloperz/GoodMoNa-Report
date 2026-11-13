namespace GoodMoNa.Server.Infrastructure;

public sealed class AppOptions
{
    public const string Section = "App";

    /// <summary>SQLite 파일과 사진을 둘 폴더. 비우면 %LOCALAPPDATA%\GoodMoNa. OneDrive 동기화 폴더는 피하세요.</summary>
    public string? DataRoot { get; set; }

    /// <summary>QR에 넣을 외부 주소 (예: https://report.example.com). 비우면 요청이 들어온 주소를 씁니다.</summary>
    public string? PublicBaseUrl { get; set; }

    public int CaptureTokenMinutes { get; set; } = 10;

    public long MaxPhotoBytes { get; set; } = 15 * 1024 * 1024;

    /// <summary>DB가 비어 있을 때 예시 환자·과거 방문 사진을 넣습니다. 실제 환자 데이터를 쓰기 전엔 끄세요.</summary>
    public bool SeedDemoData { get; set; }

    public string TimeZone { get; set; } = "Asia/Seoul";
}

public sealed class DataPaths
{
    public DataPaths(AppOptions options)
    {
        Root = string.IsNullOrWhiteSpace(options.DataRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoodMoNa")
            : Path.GetFullPath(options.DataRoot);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "goodmona.db");
    public string ConnectionString => $"Data Source={DatabaseFile}";

    /// <summary>상대 경로를 데이터 폴더 안의 절대 경로로 바꿉니다. 폴더 밖을 가리키면 거부합니다.</summary>
    public string Resolve(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("데이터 폴더 밖의 경로입니다.");
        return full;
    }
}
