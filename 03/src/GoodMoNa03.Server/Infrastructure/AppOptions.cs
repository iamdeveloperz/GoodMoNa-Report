namespace GoodMoNa03.Server.Infrastructure;

public sealed class AppOptions
{
    public const string Section = "App";

    /// <summary>SQLite 파일과 영상을 둘 폴더. 비우면 %LOCALAPPDATA%\GoodMoNa03. OneDrive 동기화 폴더는 피하세요(영상이 큼).</summary>
    public string? DataRoot { get; set; }

    public string TimeZone { get; set; } = "Asia/Seoul";

    /// <summary>촬영본 한 개의 최대 크기. 폰 4K 1분 영상이 400MB 안팎입니다.</summary>
    public int MaxUploadMegabytes { get; set; } = 2048;

    /// <summary>촬영본 최대 길이. 쇼츠 1편용 원본이라 길면 잘못 올린 것(롱폼 추출은 2단계).</summary>
    public int MaxSourceMinutes { get; set; } = 5;

    public AuthOptions Auth { get; set; } = new();
    public WhisperOptions Whisper { get; set; } = new();
    public FfmpegOptions Ffmpeg { get; set; } = new();
    public EditOptions Edit { get; set; } = new();
    public RenderOptions Render { get; set; } = new();
    public CopyOptions Copy { get; set; } = new();
    public AiOptions Ai { get; set; } = new();

    /// <summary>
    /// Whisper가 자주 틀리는 표기 → 바른 표기. 자막·제목 초안에 그대로 적용합니다.
    /// 예: "미녹 시딜" → "미녹시딜". 설정에서 더하거나 바꿀 수 있습니다.
    /// </summary>
    public Dictionary<string, string> Terms { get; set; } = new()
    {
        ["미녹 시딜"] = "미녹시딜",
        ["미녹시 딜"] = "미녹시딜",
        ["피나스 테리드"] = "피나스테리드",
        ["피나 스테리드"] = "피나스테리드",
        ["두타스 테리드"] = "두타스테리드",
        ["두타 스테리드"] = "두타스테리드",
        ["프로 페시아"] = "프로페시아",
        ["아보 다트"] = "아보다트",
        ["비절 개"] = "비절개",
        ["모발 이식"] = "모발이식",
        ["엑소 좀"] = "엑소좀",
        // 2026-11-16 실제 쇼츠 받아쓰기에서 나온 오표기
        ["클라스코테로"] = "클라스코테론",
        ["클라스 코테론"] = "클라스코테론",
    };
}

/// <summary>로그인은 02 인트라넷 서버가 맡습니다. 03은 02가 발급한 출입증(JWT)을 확인만 합니다(01과 같은 방식).</summary>
public sealed class AuthOptions
{
    /// <summary>02 서버 주소(같은 PC). 서명 공개키를 여기서 받고, localhost로 직접 열었을 때 로그인 화면도 여기로 보냅니다.</summary>
    public string Authority { get; set; } = "http://localhost:5002";

    /// <summary>02 설정과 같아야 합니다.</summary>
    public string Issuer { get; set; } = "goodmona-intranet";
    public string Audience { get; set; } = "goodmona";
}

public sealed class WhisperOptions
{
    /// <summary>01과 같은 모델 파일을 씁니다.</summary>
    public string ModelPath { get; set; } = @"C:\GoodMoNa\models\ggml-large-v3-turbo.bin";

    public string Language { get; set; } = "ko";

    public bool UseGpu { get; set; } = true;

    /// <summary>0이면 CPU 코어 수의 절반.</summary>
    public int Threads { get; set; }

    /// <summary>전문 용어 표기를 맞추기 위한 힌트 문장 (Whisper initial prompt).</summary>
    public string Prompt { get; set; } =
        "탈모 치료와 모발이식을 설명하는 의사의 짧은 영상입니다. 미녹시딜, 피나스테리드, 두타스테리드, 프로페시아, 아보다트, 클라스코테론, DHT, 비절개 모발이식, 모낭, 정수리, 헤어라인, 두피, 임상, 국내 출시.";
}

public sealed class FfmpegOptions
{
    /// <summary>ffmpeg.exe 경로. PATH에 있으면 이름만.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>h264_nvenc(NVIDIA GPU) · libx264(CPU). GPU 인코딩이 실패하면 libx264로 한 번 더 시도합니다.</summary>
    public string VideoEncoder { get; set; } = "h264_nvenc";

    /// <summary>ffmpeg 한 번 실행의 최대 시간.</summary>
    public int TimeoutMinutes { get; set; } = 20;
}

/// <summary>컷 편집(무음 자르기)과 자막 나누기 기준.</summary>
public sealed class EditOptions
{
    /// <summary>이보다 긴 무음만 자릅니다.</summary>
    public int MinSilenceMs { get; set; } = 400;

    /// <summary>자른 자리 앞뒤로 남기는 여유. 너무 짧으면 말끝이 잘립니다.</summary>
    public int KeepPadMs { get; set; } = 150;

    /// <summary>이 음량(dB)보다 작으면 무음으로 봅니다. 진료실 잡음이 크면 -30 쪽으로.</summary>
    public int SilenceNoiseDb { get; set; } = -35;

    /// <summary>자막 한 줄 최대 글자 수(공백 포함).</summary>
    public int MaxLineChars { get; set; } = 12;

    /// <summary>자막 한 장의 최대 줄 수.</summary>
    public int MaxLines { get; set; } = 2;

    /// <summary>단어 사이가 이만큼 벌어지면 자막 장을 새로 시작합니다.</summary>
    public int CueGapMs { get; set; } = 600;
}

public sealed class RenderOptions
{
    /// <summary>
    /// 자막 글꼴 이름. 기본은 모든 Windows에 있는 맑은 고딕.
    /// Pretendard 등을 쓰려면 글꼴 파일을 <see cref="FontsDir"/>에 두고 이름을 바꾸세요(상업 사용 가능한 글꼴만).
    /// </summary>
    public string FontName { get; set; } = "Malgun Gothic";

    /// <summary>글꼴 파일 폴더(비우면 시스템 글꼴).</summary>
    public string FontsDir { get; set; } = "";

    /// <summary>끝 화면(관련 영상 안내 + 영상 코드)을 보여 줄 시간.</summary>
    public double EndCardSeconds { get; set; } = 2.5;

    /// <summary>끝 화면 첫 줄. 둘째 줄은 "상담 시 코드 S1116A" 형식으로 자동.</summary>
    public string EndCardText { get; set; } = "전체 설명은 아래 관련 영상에서";
}

/// <summary>제목·설명 초안.</summary>
public sealed class CopyOptions
{
    /// <summary>상담 랜딩 주소. 설명란 템플릿의 {landingUrl}에 UTM을 붙여 넣습니다.</summary>
    public string LandingUrl { get; set; } = "";

    public List<string> DefaultHashtags { get; set; } = ["#탈모", "#모발이식", "#탈모치료"];

    /// <summary>
    /// 설명란 템플릿. {summary} 영상 요약 · {code} 영상 코드 · {landingUrl} UTM 붙은 상담 주소 · {hashtags}.
    /// 쇼츠 설명란 링크는 눌리지 않으므로(2023-08-31~) 기본 템플릿은 링크 대신 영상 코드를 안내합니다.
    /// </summary>
    public string DescriptionTemplate { get; set; } = "{summary}\n\n상담 문의 때 영상 코드 {code}를 말씀해 주세요.\n\n{hashtags}";

    /// <summary>제목 최대 길이(유튜브 제한 100자, 쇼츠는 짧을수록 잘 보임).</summary>
    public int MaxTitleChars { get; set; } = 40;
}

/// <summary>제목·훅·점검 초안을 쓰는 AI. 02의 CS 도우미와 같은 방식(Off / Ollama 로컬 / Claude API).</summary>
public sealed class AiOptions
{
    /// <summary>Off | Ollama | Claude. Off면 규칙으로만 초안을 만듭니다.</summary>
    public string Mode { get; set; } = "Off";

    public string OllamaUrl { get; set; } = "http://localhost:11434";

    /// <summary>Ollama는 필수, Claude는 비우면 claude-opus-5-5.</summary>
    public string Model { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 90;

    public string KeepAlive { get; set; } = "10m";

    /// <summary>Claude API 키. 리포에 두지 말고 데이터 폴더의 appsettings.local.json에만.</summary>
    public string? ApiKey { get; set; }

    public bool ClaudeEnabled => Mode.Equals("Claude", StringComparison.OrdinalIgnoreCase);

    public bool OllamaEnabled => Mode.Equals("Ollama", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Model);
}

public sealed class DataPaths
{
    public DataPaths(AppOptions options)
    {
        Root = string.IsNullOrWhiteSpace(options.DataRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GoodMoNa03")
            : Path.GetFullPath(options.DataRoot);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MediaRoot);
    }

    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "shorts.db");
    public string ConnectionString => $"Data Source={DatabaseFile}";

    /// <summary>원본·음성·자막·미리보기·완성본. 초안마다 하위 폴더 하나.</summary>
    public string MediaRoot => Path.Combine(Root, "media");

    public string SourceFolder(int sourceId) => Path.Combine(MediaRoot, $"src-{sourceId}");
}

/// <summary>병원 기준 시각(영상 코드의 날짜, 화면 표시).</summary>
public sealed class ClinicClock(TimeProvider time, Microsoft.Extensions.Options.IOptions<AppOptions> options)
{
    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(UtcNow, _zone);

    public DateOnly Today => DateOnly.FromDateTime(LocalNow);
}
