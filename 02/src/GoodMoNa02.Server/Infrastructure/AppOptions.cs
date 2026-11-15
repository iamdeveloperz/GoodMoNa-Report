namespace GoodMoNa02.Server.Infrastructure;

public sealed class AppOptions
{
    public const string Section = "App";

    /// <summary>SQLite·서명 키를 둘 폴더. 비우면 %LOCALAPPDATA%\GoodMoNa02. OneDrive 동기화 폴더는 피하세요.</summary>
    public string? DataRoot { get; set; }

    public string TimeZone { get; set; } = "Asia/Seoul";

    public AuthOptions Auth { get; set; } = new();

    public AssistantOptions Assistant { get; set; } = new();
}

/// <summary>
/// CS 안내 도우미의 AI 답변. 기본은 꺼짐(검색 결과만).
/// Mode = "Claude": Claude API(질문·찾은 자료가 Anthropic으로 전송, ApiKey 필요, Model 비우면 claude-opus-5-5).
/// Mode = "Ollama": 서버 PC의 로컬 모델(Model 필수, 외부 전송 없음).
/// 운영에서는 데이터 폴더의 appsettings.local.json에 둡니다(배포가 덮어쓰지 않음).
/// </summary>
public sealed class AssistantOptions
{
    /// <summary>Off | Claude | Ollama</summary>
    public string Mode { get; set; } = "Off";

    public string OllamaUrl { get; set; } = "http://localhost:11434";

    /// <summary>모델 이름. Ollama는 필수(상업 사용 가능한 한국어 지원 모델), Claude는 비우면 claude-opus-5-5</summary>
    public string Model { get; set; } = "";

    /// <summary>답변을 기다리는 최대 시간. 넘기면 검색 결과만 보여 줌</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Ollama 모델을 GPU에 올려 둘 시간(Ollama keep_alive). 음수(-1m)면 계속 올려 둠 — 기본.
    /// Ollama 기본(5분)으로 두면 한동안 질문이 없다가 온 첫 질문이 모델을 다시 올리느라 제한 시간을 넘길 수 있습니다.
    /// </summary>
    public string KeepAlive { get; set; } = "-1m";

    /// <summary>AI에게 넘길 근거 자료 수 (작은 모델은 적을수록 정확)</summary>
    public int MaxPassages { get; set; } = 4;

    /// <summary>Claude API 키. 리포·설치 폴더에 두지 말고 데이터 폴더의 appsettings.local.json에만 (deploy/set-assistant.ps1 -Mode Claude)</summary>
    public string? ApiKey { get; set; }

    public bool ClaudeEnabled => Mode.Equals("Claude", StringComparison.OrdinalIgnoreCase);

    public bool OllamaEnabled => Mode.Equals("Ollama", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Model);
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
