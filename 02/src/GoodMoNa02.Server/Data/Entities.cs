namespace GoodMoNa02.Server.Data;

/// <summary>
/// 직무(역할). 출입증(토큰)의 role 값으로 그대로 나가며, 01 진료 서버도 같은 이름으로 권한을 나눕니다.
/// 이름을 바꾸면 01의 <c>Auth/StaffRoles.cs</c>도 함께 바꿔야 합니다.
/// </summary>
public enum StaffRole
{
    /// <summary>대표원장: 모든 권한 + 직원 승인</summary>
    Owner,
    /// <summary>원장: 결과 확인·소견 녹음</summary>
    Doctor,
    /// <summary>간호사: 환자 검색·촬영·보고</summary>
    Nurse,
    /// <summary>일반 직원(CS·코디·행정)</summary>
    Staff,
    /// <summary>관리자: 직원 승인·이전 기록 가져오기</summary>
    Admin
}

public enum StaffStatus
{
    /// <summary>가입 신청 후 승인 대기. 로그인할 수 없습니다.</summary>
    Pending,
    Active,
    Rejected,
    /// <summary>퇴사·사용 중지. 로그인과 출입증 갱신이 막힙니다.</summary>
    Disabled
}

public sealed class Staff
{
    public int Id { get; set; }

    /// <summary>로그인 아이디 (소문자로 저장, 유일)</summary>
    public required string LoginId { get; set; }
    public required string Name { get; set; }
    public required string PasswordHash { get; set; }

    /// <summary>승인된 직무. 승인 전에는 의미가 없습니다.</summary>
    public StaffRole Role { get; set; }

    /// <summary>가입할 때 본인이 고른 직무. 승인하는 사람이 바꿔서 승인할 수 있습니다.</summary>
    public StaffRole RequestedRole { get; set; }
    public StaffStatus Status { get; set; }
    public string? Department { get; set; }
    public string? Phone { get; set; }

    public int FailedLogins { get; set; }
    public DateTime? LockedUntil { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>승인·반려·중지 같은 마지막 결정 시각과 결정한 사람</summary>
    public DateTime? DecidedAt { get; set; }
    public int? DecidedById { get; set; }
    public string? RejectReason { get; set; }

    public List<StaffSession> Sessions { get; set; } = [];
}

/// <summary>
/// 로그인 유지용 갱신 토큰(브라우저 쿠키 gm_refresh). 원문은 저장하지 않고 SHA-256 해시만 둡니다.
/// 갱신할 때마다 새 토큰으로 바꾸고(회전), 이미 바뀐 토큰이 다시 쓰이면 도난으로 보고 그 직원의 세션을 모두 끊습니다.
/// </summary>
public sealed class StaffSession
{
    public int Id { get; set; }
    public int StaffId { get; set; }
    public Staff Staff { get; set; } = null!;
    public required string TokenHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>갱신으로 새 토큰에 자리를 넘긴 시각. 로그아웃·퇴사·도난으로 끊긴 세션은 비어 있습니다.</summary>
    public DateTime? ReplacedAt { get; set; }
}
