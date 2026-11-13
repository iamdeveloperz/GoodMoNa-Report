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

    /// <summary>원장 녹음 한 개의 최대 길이. 화면 녹음기는 10분에서 자동으로 멈춥니다.</summary>
    public int MaxNoteMinutes { get; set; } = 12;

    public WhisperOptions Whisper { get; set; } = new();
}

public sealed class WhisperOptions
{
    /// <summary>ggml 모델 파일. large-v3-turbo가 한국어 정확도·속도 균형이 가장 좋습니다.</summary>
    public string ModelPath { get; set; } = @"C:\GoodMoNa\models\ggml-large-v3-turbo.bin";

    public string Language { get; set; } = "ko";

    /// <summary>GPU(Vulkan)를 먼저 시도하고, 안 되면 CPU로 돌립니다.</summary>
    public bool UseGpu { get; set; } = true;

    /// <summary>0이면 CPU 코어 수에 맞춥니다.</summary>
    public int Threads { get; set; }

    /// <summary>전문 용어 표기를 맞추기 위한 힌트 문장 (Whisper initial prompt).</summary>
    public string Prompt { get; set; } =
        "두피·탈모 클리닉 원장 소견 녹음입니다. 미녹시딜, 피나스테리드, 두타스테리드, PRP, 엑소좀, 두피 스케일링, 정수리, 헤어라인, 모발 밀도, 홍반, 각질, 내원, 처방.";
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

    /// <summary>이전 기록 가져오기용 사진을 모아 두는 폴더. 웹에서 올리거나, 사진이 많으면 이 폴더에 직접 복사합니다.</summary>
    public string ImportRoot => Path.Combine(Root, "import");

    /// <summary>상대 경로를 데이터 폴더 안의 절대 경로로 바꿉니다. 폴더 밖을 가리키면 거부합니다.</summary>
    public string Resolve(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("데이터 폴더 밖의 경로입니다.");
        return full;
    }
}
