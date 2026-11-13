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

/// <summary>원장 앱(2번 과제)이 받는 새 보고 알림.</summary>
public sealed record ReportSubmittedEvent(
    int VisitId,
    string PatientName,
    DateOnly BirthDate,
    Progress Progress,
    DateTime ReportedAt);
