namespace GoodMoNa.Server.Contracts;

/// <summary>CSV 한 행의 문제. Row는 엑셀 행 번호(머리글 = 1행)입니다.</summary>
public sealed record ImportIssue(int Row, string Message);

/// <summary>가져오기 전 미리보기. 아무것도 저장하지 않습니다.</summary>
public sealed record ImportPreviewDto(
    string Encoding,
    int Rows,
    int Patients,
    int NewPatients,
    int Visits,
    int NewVisits,
    int Photos,
    int ErrorRows,
    IReadOnlyList<ImportIssue> Errors,
    IReadOnlyList<ImportIssue> Warnings);

public sealed record ImportResultDto(
    int NewPatients,
    int NewVisits,
    int UpdatedVisits,
    int PhotosAdded,
    int PhotosSkipped,
    int SkippedRows);

public enum ImportState
{
    Idle,
    Running,
    Succeeded,
    Failed
}

public sealed record ImportStatusDto(
    ImportState State,
    int Done,
    int Total,
    int PhotosDone,
    int PhotosTotal,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    ImportResultDto? Result,
    string? Error);

public sealed record ImportFileDto(string Path, long Size);

public sealed record ImportFolderDto(string Root, int Count, long TotalBytes, IReadOnlyList<ImportFileDto> Files);
