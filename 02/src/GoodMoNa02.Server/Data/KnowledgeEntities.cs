namespace GoodMoNa02.Server.Data;

/* ===================== CS 안내 도우미: 지식 자료 ===================== */

public enum KbKind
{
    /// <summary>짧은 질문·답 한 쌍</summary>
    Faq,
    /// <summary>안내 문서 한 덩어리 (제목 + 본문)</summary>
    Doc
}

/// <summary>자료 상태. 승인(Published)된 자료만 검색·AI 답변의 근거가 됩니다.</summary>
public enum KbStatus
{
    /// <summary>초안: 가져오기·직원 제안·질문에서 만든 것. 관리자 검토 전</summary>
    Draft,
    Published,
    Rejected,
    /// <summary>내림: 예전에 쓰던 자료 (검색 안 됨, 기록은 남음)</summary>
    Archived
}

public enum KbSource
{
    /// <summary>관리자·직원이 직접 작성</summary>
    Manual,
    /// <summary>자료 가져오기(붙여넣기·파일, 클라우드 AI가 정리한 결과 포함)</summary>
    Import,
    /// <summary>답을 못 찾은 직원 질문에서 만든 FAQ</summary>
    Question
}

/// <summary>지식 자료 한 건. FAQ면 Title = 질문, Body = 답.</summary>
public sealed class KbItem
{
    public int Id { get; set; }
    public KbKind Kind { get; set; }
    public KbStatus Status { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }

    /// <summary>분류 (예약·주차·결제·서류 …). 비우면 "일반"</summary>
    public string Category { get; set; } = "일반";

    /// <summary>검색을 돕는 다른 표현, 쉼표로 구분 (예: "주차장, 차, 발렛")</summary>
    public string Keywords { get; set; } = "";

    public KbSource Source { get; set; }

    /// <summary>원본 이름 (가져온 파일명, 질문 번호 등)</summary>
    public string? SourceRef { get; set; }

    public int? ImportId { get; set; }
    public KbImport? Import { get; set; }

    /// <summary>재검토일: 가격·이벤트처럼 바뀌는 정보는 이 날이 지나면 "재검토 필요"로 표시</summary>
    public DateOnly? ReviewBy { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedById { get; set; }
    public required string CreatedByName { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>승인·반려·내림을 결정한 사람과 시각</summary>
    public DateTime? DecidedAt { get; set; }
    public int? DecidedById { get; set; }
    public string? DecidedByName { get; set; }
    public string? RejectReason { get; set; }
}

/// <summary>가져오기 한 번 (자료 묶음). 어디서 왔는지 남겨 둡니다.</summary>
public sealed class KbImport
{
    public int Id { get; set; }

    /// <summary>원본 이름 (파일명 또는 "붙여넣기")</summary>
    public required string Name { get; set; }

    /// <summary>정리한 방법: 직접 · Claude · Codex · 기타 (초기 수집에 어떤 AI를 썼는지 기록)</summary>
    public required string Origin { get; set; }

    public int ItemCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public required string CreatedByName { get; set; }
}

/// <summary>질문 기록의 후속 처리 상태 (답을 못 찾았거나 "부족함"을 누른 질문만 Open)</summary>
public enum AskFollowUp
{
    None,
    Open,
    Resolved,
    Dismissed
}

/// <summary>직원 질문 한 번. 못 찾은 질문은 관리자에게 모여 FAQ가 됩니다(학습 고리).</summary>
public sealed class AskLog
{
    public int Id { get; set; }
    public int StaffId { get; set; }
    public required string StaffName { get; set; }

    /// <summary>질문 (전화번호·주민번호는 가려서 저장)</summary>
    public required string Question { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>근거 자료를 찾았는지</summary>
    public bool Found { get; set; }

    /// <summary>가장 잘 맞은 자료와 일치 정도(0~1)</summary>
    public int? TopItemId { get; set; }
    public double TopCoverage { get; set; }

    /// <summary>AI 답변을 보여 줬는지</summary>
    public bool Answered { get; set; }

    /// <summary>직원 평가: 도움됨(true) · 부족함(false) · 안 누름(null)</summary>
    public bool? Helpful { get; set; }

    public AskFollowUp FollowUp { get; set; }

    /// <summary>이 질문으로 만든 자료</summary>
    public int? ResolvedItemId { get; set; }

    /// <summary>자료는 찾았지만 AI가 "자료에 없음"이라고 판단 (검색이 엉뚱한 자료를 집은 경우) → 관리자 확인</summary>
    public bool AiDeclined { get; set; }

    /// <summary>직원이 적은 맞는 답 (정정). 자동 정리가 이것으로 FAQ 초안을 만듭니다.</summary>
    public string? Correction { get; set; }

    /// <summary>정정으로 만든 FAQ 초안. 승인되면 이 질문은 처리됨</summary>
    public int? DraftItemId { get; set; }

    /// <summary>자동 정리가 이 질문을 살펴본 시각 (같은 질문을 밤마다 다시 묻지 않게)</summary>
    public DateTime? CuratedAt { get; set; }
}

/* ===================== 자동 정리 (밤마다 · 지금 정리하기) ===================== */

public enum KbSuggestionKind
{
    /// <summary>못 찾은 질문 표현을 이미 있는 자료의 검색 보조어로 붙이자는 제안</summary>
    Keywords,
    /// <summary>병원 홈페이지 내용이 바뀌어 자료를 고치자는 제안</summary>
    SiteChange
}

public enum KbSuggestionStatus
{
    Pending,
    Accepted,
    Dismissed
}

/// <summary>자동 정리가 만든 제안. 관리자가 "반영"해야 자료가 바뀝니다(사람이 마지막에 확인).</summary>
public sealed class KbSuggestion
{
    public int Id { get; set; }
    public KbSuggestionKind Kind { get; set; }
    public KbSuggestionStatus Status { get; set; }

    /// <summary>바꿀 자료 (홈페이지 변경인데 맞는 자료를 못 찾았으면 비어 있음)</summary>
    public int? ItemId { get; set; }

    /// <summary>한 줄 요약</summary>
    public required string Title { get; set; }

    /// <summary>근거: 붙일 질문들 · 홈페이지에서 바뀐 줄</summary>
    public string Detail { get; set; } = "";

    /// <summary>Keywords: 붙일 표현(줄마다 하나)</summary>
    public string Phrases { get; set; } = "";

    /// <summary>SiteChange: AI가 고친 자료 본문 (반영하면 이것으로 바뀜)</summary>
    public string? ProposedBody { get; set; }

    /// <summary>이 제안이 처리할 질문 기록 번호들 (쉼표)</summary>
    public string AskLogIds { get; set; } = "";

    public string? SourceUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecidedByName { get; set; }
}

/// <summary>매일 확인하는 병원 홈페이지 주소. 처음 읽은 내용을 기준으로 삼고, 바뀌면 제안을 만듭니다.</summary>
public sealed class KbWatchPage
{
    public int Id { get; set; }
    public required string Url { get; set; }

    /// <summary>마지막으로 읽은 본문 글자 (줄 단위)</summary>
    public string? LastText { get; set; }

    public string? LastHash { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public DateTime? LastChangedAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
}
