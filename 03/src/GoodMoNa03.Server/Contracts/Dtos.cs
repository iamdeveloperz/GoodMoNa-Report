using GoodMoNa03.Server.Data;

namespace GoodMoNa03.Server.Contracts;

public sealed record DraftListItemDto(
    int Id, string Code, DraftStatus Status, string? Stage, string? Error, string Title, string OriginalName,
    int DurationMs, int OutputDurationMs, int OpenFindings, int PreviewVersion, string UploadedByName, DateTime CreatedAt);

public sealed record SourceDto(string OriginalName, long SizeBytes, int DurationMs, int Width, int Height,
    string UploadedByName, DateTime CreatedAt, string? SttModel, string? SttRuntime, int SttElapsedMs);

public sealed record CueDto(int Id, int StartMs, int EndMs, string Text);

public sealed record CutDto(int Index, int StartMs, int EndMs, bool Removed);

public sealed record FindingDto(int Id, FindingSource Source, string Where, string Rule, string Excerpt, string Reason, bool Blocking,
    string? Resolution, string? ResolvedByName, DateTime? ResolvedAt);

public sealed record DraftDto(
    int Id, string Code, DraftStatus Status, string? Stage, string? Error, int Version, int PreviewVersion,
    SourceDto Source, int OutputDurationMs,
    string Hook, IReadOnlyList<string> HookCandidates, string Title, IReadOnlyList<string> TitleCandidates,
    string Summary, string Description, string Hashtags, IReadOnlyList<string> Emphasis, string? AiNote,
    bool HasPatient, bool ConsentConfirmed,
    IReadOnlyList<CueDto> Cues, IReadOnlyList<CutDto> Cuts, IReadOnlyList<FindingDto> Findings,
    IReadOnlyList<string> ApprovalBlockers,
    string? UpdatedByName, DateTime UpdatedAt,
    string? ApprovedByName, DateTime? ApprovedAt,
    string? RejectReason, string? RejectedByName,
    string? YouTubeUrl, bool RelatedVideoLinked, DateTime? PublishedAt, string? PublishedByName);

public sealed record CueEdit(int Id, string Text);

public sealed record CutEdit(int Index, bool Removed);

/// <summary>검토 화면 저장. 보낸 칸만 바꿉니다. Version은 화면이 읽었던 버전(다르면 409).</summary>
public sealed record EditDraftRequest(
    int Version, string? Title, string? Hook, string? Description, string? Hashtags,
    bool? HasPatient, bool? ConsentConfirmed, IReadOnlyList<CueEdit>? Cues, IReadOnlyList<CutEdit>? Cuts);

public sealed record ResolveRequest(string Note);

public sealed record ApproveRequest(int Version);

public sealed record RejectRequest(string Reason);

public sealed record PublishRequest(string YouTubeUrl, bool RelatedVideoLinked);

public sealed record TitleImportRequest(string Text);

public sealed record TitleImportResult(int Added, int Updated, int Total);

/// <summary>설정 상태(화면 위쪽 안내). 무엇이 빠져 있는지 바로 보이게.</summary>
public sealed record SystemStatusDto(string? Ffmpeg, bool WhisperModel, string Ai, bool AiEnabled, int MaxUploadMegabytes,
    int TitleHistoryCount, bool CanApprove);
