using GoodMoNa02.Server.Data;

namespace GoodMoNa02.Server.Contracts;

/* ---------- CS 안내 도우미: 지식 자료 · 질문 ---------- */

public static class KbLimits
{
    public const int Title = 200;
    public const int Body = 4000;
    public const int Category = 20;
    public const int Keywords = 200;
    public const int Question = 500;

    /// <summary>가져오기 한 번에 붙여넣을 수 있는 글자 수 / 만들 수 있는 자료 수</summary>
    public const int ImportText = 200_000;
    public const int ImportItems = 300;
}

public sealed record KbItemDto(
    int Id, KbKind Kind, KbStatus Status, string Title, string Body, string Category, string Keywords,
    KbSource Source, string? SourceRef, int? ImportId, DateOnly? ReviewBy, bool Stale,
    DateTime CreatedAt, string CreatedByName, DateTime? UpdatedAt,
    DateTime? DecidedAt, string? DecidedByName, string? RejectReason);

/// <summary>자료 작성·수정. 직원은 제안(초안)만, 관리자는 바로 승인(Publish)도 가능. FromQuestionId: 못 찾은 질문으로 만든 FAQ</summary>
public sealed record KbItemRequest(
    KbKind? Kind, string? Title, string? Body, string? Category, string? Keywords, string? ReviewBy,
    bool Publish = false, int? FromQuestionId = null);

public sealed record KbRejectRequest(string? Reason);

public sealed record KbIdsRequest(int[]? Ids);

/// <summary>못 찾은 질문을 이 자료의 검색 보조어로 붙이기</summary>
public sealed record KbAttachRequest(int ItemId);

/// <summary>자료 가져오기: 붙여넣은 글(또는 파일 내용) → 규칙으로 잘라 초안 묶음</summary>
public sealed record KbImportRequest(string? Text, string? Name, string? Origin, string? Category);

public sealed record KbDraftDto(KbKind Kind, string Title, string Body, string Category, string Keywords);

public sealed record KbImportPreviewDto(List<KbDraftDto> Items, List<string> Warnings);

public sealed record KbImportDto(int Id, string Name, string Origin, int ItemCount, DateTime CreatedAt, string CreatedByName);

public sealed record KbImportResultDto(KbImportDto Import, List<KbItemDto> Items, List<string> Warnings);

public sealed record KbOverviewDto(int Drafts, int Published, int Stale, int OpenQuestions, AssistantStatusDto Assistant);

/* ---------- 질문 ---------- */

public sealed record AskRequest(string? Question);

/// <summary>검색으로 찾은 자료 한 건. Coverage = 질문 낱말 중 이 자료에 있는 비율(0~1)</summary>
public sealed record AskHitDto(int Id, KbKind Kind, string Title, string Category, string Snippet, string Body, double Coverage, DateOnly? ReviewBy, bool Stale);

/// <summary>AI 답변. 본문의 [1]·[2]는 Sources의 번호</summary>
public sealed record AskAnswerDto(string Text, List<AskSourceDto> Sources);

public sealed record AskSourceDto(int N, int ItemId, string Title);

/// <summary>
/// 질문 결과. Found면 Faqs·Docs가 근거, 아니면 Related(조금이라도 겹치는 자료)만 참고로 보여 주고 질문은 관리자에게 넘어갑니다.
/// </summary>
public sealed record AskResultDto(int LogId, string Question, bool Found, List<AskHitDto> Faqs, List<AskHitDto> Docs, List<AskHitDto> Related, AskAnswerDto? Answer, string? Note);

public sealed record AskFeedbackRequest(bool Helpful);

public sealed record AskLogDto(
    int Id, string Question, int StaffId, string StaffName, DateTime CreatedAt, bool Found, bool Answered, bool? Helpful,
    AskFollowUp FollowUp, int? TopItemId, string? TopItemTitle, double TopCoverage, int? ResolvedItemId);

/// <summary>AI 답변 기능 상태. Enabled가 false면 검색 결과만 보여 줍니다.</summary>
public sealed record AssistantStatusDto(bool Enabled, string Mode, string? Model);
