using GoodMoNa.Server.Data;

namespace GoodMoNa.Server.Contracts;

public sealed record PatientSummaryDto(
    int Id,
    string Name,
    DateOnly BirthDate,
    DateOnly? LastVisitDate,
    VisitStatus? TodayStatus);

public sealed record RegisterPatientRequest(string? Name, string? Birth);

public sealed record PatientDto(int Id, string Name, DateOnly BirthDate);

public sealed record PhotoDto(int Id, string Url, DateTime TakenAt);

public sealed record PreviousPhotoDto(DateOnly VisitDate, int DaysAgo, PhotoDto Photo);

public sealed record VisitDto(
    int Id,
    PatientDto Patient,
    DateOnly VisitDate,
    VisitStatus Status,
    Progress? Progress,
    string Memo,
    PhotoDto? Photo,
    PreviousPhotoDto? Previous,
    DateTime? ReportedAt,
    string? ReportedBy);

public sealed record SubmitReportRequest(Progress? Progress, string? Memo);

public sealed record CaptureTokenDto(string Token, string CaptureUrl, DateTime ExpiresAt);

public enum CaptureState
{
    Ready,
    Expired,
    Reported
}

/// <summary>폰 화면에 보이는 정보. 폰은 병원 밖 기기일 수 있으므로 이름은 가려서 보냅니다.</summary>
public sealed record CaptureInfoDto(
    string MaskedName,
    DateOnly BirthDate,
    DateOnly VisitDate,
    DateTime ExpiresAt,
    CaptureState State);

public sealed record SegmentDto(int StartMs, int EndMs, string Text);

public sealed record TranscriptDto(string Text, IReadOnlyList<SegmentDto> Segments, string Model, string Runtime, int ElapsedMs, DateTime CreatedAt);

/// <summary>녹음 하나와, 변환이 끝났다면 그에 딸린 스크립트.</summary>
public sealed record NoteDto(
    int Id,
    int VisitId,
    int Seq,
    NoteStatus Status,
    string? Error,
    int DurationMs,
    NoteSource Source,
    DateTime CreatedAt,
    string AudioUrl,
    TranscriptDto? Transcript);

/// <summary>원장 앱 왼쪽 목록의 한 줄.</summary>
public sealed record ReportListItemDto(
    int VisitId,
    int PatientId,
    string PatientName,
    DateOnly BirthDate,
    Progress? Progress,
    DateTime ReportedAt,
    string? ReportedBy,
    int NoteCount,
    int PendingNoteCount);

/// <summary>환자 기록 재구성: 방문별 사진·경과·메모와 녹음 스크립트를 날짜순으로 묶은 것.</summary>
public sealed record HistoryVisitDto(
    int VisitId,
    DateOnly VisitDate,
    VisitStatus Status,
    Progress? Progress,
    string Memo,
    PhotoDto? Photo,
    IReadOnlyList<NoteDto> Notes);

public sealed record PatientHistoryDto(PatientDto Patient, IReadOnlyList<HistoryVisitDto> Visits);

/// <summary>원장 앱이 받는 새 보고 알림.</summary>
public sealed record ReportSubmittedEvent(
    int VisitId,
    string PatientName,
    DateOnly BirthDate,
    Progress Progress,
    DateTime ReportedAt);
