namespace GoodMoNa02.Server.Infrastructure;

public sealed class AppOptions
{
    public const string Section = "App";

    /// <summary>SQLite와 파일을 둘 폴더. 비우면 %LOCALAPPDATA%\GoodMoNa02. OneDrive 동기화 폴더는 피하세요.</summary>
    public string? DataRoot { get; set; }

    public string TimeZone { get; set; } = "Asia/Seoul";
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

    /// <summary>상대 경로를 데이터 폴더 안의 절대 경로로 바꿉니다. 폴더 밖을 가리키면 거부합니다.</summary>
    public string Resolve(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("데이터 폴더 밖의 경로입니다.");
        return full;
    }
}
