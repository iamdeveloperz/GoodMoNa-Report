namespace GoodMoNa03.Server.Data;

/// <summary>
/// 초안 상태.
/// Processing(분석 중) → Rendering(미리보기 만드는 중) → Review(검토 대기) → Approved(승인, 완성본 만드는 중) → Ready(완성본 준비) → Published(게시 완료).
/// Review에서 Rejected(반려), 어느 단계든 실패하면 Failed.
/// </summary>
public enum DraftStatus
{
    Processing,
    Rendering,
    Review,
    Approved,
    Ready,
    Published,
    Rejected,
    Failed,
}

public enum FindingSource
{
    /// <summary>규칙 사전(정규식)</summary>
    Rule,

    /// <summary>AI 2차 점검</summary>
    Ai,

    /// <summary>기존 제목과 비슷함(참고용, 승인을 막지 않음)</summary>
    Similar,
}

/// <summary>원장이 찍은 촬영본 하나. 1단계에서는 촬영본 1개 = 쇼츠 초안 1개.</summary>
public sealed class SourceVideo
{
    public int Id { get; set; }
    public required string OriginalName { get; set; }

    /// <summary>데이터 폴더의 src-{Id} 안 파일 이름 (source.mp4 등)</summary>
    public required string StoredName { get; set; }

    public long SizeBytes { get; set; }
    public int DurationMs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>받아쓰기 단어(원본 기준 시간) JSON</summary>
    public string WordsJson { get; set; } = "[]";

    public string? SttModel { get; set; }
    public string? SttRuntime { get; set; }
    public int SttElapsedMs { get; set; }

    public int? UploadedById { get; set; }
    public required string UploadedByName { get; set; }
    public DateTime CreatedAt { get; set; }

    public ShortDraft? Draft { get; set; }
}

public sealed class ShortDraft
{
    public int Id { get; set; }
    public int SourceId { get; set; }
    public SourceVideo Source { get; set; } = null!;

    /// <summary>영상 코드(S1116A). 상담 유입 추적용, 끝 화면·설명란에 들어감</summary>
    public required string Code { get; set; }

    public DraftStatus Status { get; set; }

    /// <summary>진행 중 단계 설명("음성 인식 중")</summary>
    public string? Stage { get; set; }

    public string? Error { get; set; }

    /// <summary>자를 구간(무음) 목록 JSON — 검토에서 켜고 끔</summary>
    public string CutsJson { get; set; } = "[]";

    /// <summary>자막 장 목록 JSON(원본 기준 시간)</summary>
    public string CuesJson { get; set; } = "[]";

    public string HookCandidatesJson { get; set; } = "[]";
    public string Hook { get; set; } = "";
    public string TitleCandidatesJson { get; set; } = "[]";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>"#탈모 #모발이식"처럼 공백으로 구분</summary>
    public string Hashtags { get; set; } = "";

    public string EmphasisJson { get; set; } = "[]";

    /// <summary>AI 초안 상태 안내("AI 꺼짐 — …")</summary>
    public string? AiNote { get; set; }

    /// <summary>환자가 나오는 영상인지. 그렇다면 출연 동의 확인이 있어야 승인됨</summary>
    public bool HasPatient { get; set; }
    public bool ConsentConfirmed { get; set; }

    /// <summary>자른 뒤 길이</summary>
    public int OutputDurationMs { get; set; }

    /// <summary>수정할 때마다 1씩: 두 사람이 동시에 고칠 때 덮어쓰기 방지, 승인할 때 "본 버전"인지 확인</summary>
    public int Version { get; set; } = 1;

    /// <summary>미리보기를 다시 만들 때마다 1씩(화면 캐시 무효화)</summary>
    public int PreviewVersion { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedByName { get; set; }

    public int? ApprovedById { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public string? RejectReason { get; set; }
    public string? RejectedByName { get; set; }

    public string? YouTubeUrl { get; set; }
    public bool RelatedVideoLinked { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? PublishedByName { get; set; }

    public List<ComplianceFinding> Findings { get; set; } = [];
}

/// <summary>표현 점검 결과 한 건. Blocking이면 해소(Resolution)해야 승인 가능.</summary>
public sealed class ComplianceFinding
{
    public int Id { get; set; }
    public int DraftId { get; set; }
    public ShortDraft Draft { get; set; } = null!;

    public FindingSource Source { get; set; }
    public required string Where { get; set; }
    public required string Rule { get; set; }
    public required string Excerpt { get; set; }
    public required string Reason { get; set; }
    public bool Blocking { get; set; }

    public string? Resolution { get; set; }
    public string? ResolvedByName { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>이미 올린 쇼츠 제목(반복 제목 경고용). tools/collect-shorts.js 목록 가져오기 + 게시 완료한 초안.</summary>
public sealed class TitleHistory
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public string? VideoId { get; set; }
    public int? Views { get; set; }
    public DateTime CreatedAt { get; set; }
}
