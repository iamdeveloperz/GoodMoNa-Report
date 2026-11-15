namespace GoodMoNa02.Server.Data;

/* ===================== 공지사항 ===================== */

/// <summary>공지. 필독(Required)이면 홈 위에 고정되고, 작성자는 누가 아직 안 읽었는지 볼 수 있습니다.</summary>
public sealed class Notice
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public bool Required { get; set; }
    public int AuthorId { get; set; }

    /// <summary>작성 당시 이름 (나중에 퇴사해도 남도록)</summary>
    public required string AuthorName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public List<NoticeRead> Reads { get; set; } = [];
}

public sealed class NoticeRead
{
    public int Id { get; set; }
    public int NoticeId { get; set; }
    public Notice Notice { get; set; } = null!;
    public int StaffId { get; set; }
    public DateTime ReadAt { get; set; }
}

/* ===================== 연차 ===================== */

public enum LeaveType
{
    /// <summary>연차 (잔여에서 차감)</summary>
    Annual,
    /// <summary>병가 (차감 안 함)</summary>
    Sick,
    /// <summary>경조 휴가 (차감 안 함)</summary>
    Family,
    /// <summary>공가: 예비군·민방위·투표 등 (차감 안 함)</summary>
    Official
}

public enum LeavePortion
{
    Full,
    /// <summary>오전 반차 (0.5일)</summary>
    AmHalf,
    /// <summary>오후 반차 (0.5일)</summary>
    PmHalf
}

public enum LeaveStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled
}

/// <summary>휴가 신청 한 건. 일수는 저장하지 않고 근무 요일·휴무일로 그때그때 셉니다(휴무일을 나중에 추가해도 맞게).</summary>
public sealed class LeaveRequest
{
    public int Id { get; set; }
    public int StaffId { get; set; }
    public Staff Staff { get; set; } = null!;
    public LeaveType Type { get; set; }
    public LeavePortion Portion { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string? Reason { get; set; }
    public LeaveStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>승인·반려·취소한 시각과 사람, 메모</summary>
    public DateTime? DecidedAt { get; set; }
    public int? DecidedById { get; set; }
    public string? DecidedByName { get; set; }
    public string? DecisionNote { get; set; }
}

/// <summary>연차 수동 조정: 이월·특별휴가·도입 전 사용분 등. 적용일이 속한 연차 기간에 더하거나 뺍니다.</summary>
public sealed class LeaveAdjustment
{
    public int Id { get; set; }
    public int StaffId { get; set; }
    public Staff Staff { get; set; } = null!;
    public decimal Days { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public required string Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public int CreatedById { get; set; }
    public required string CreatedByName { get; set; }
}

/// <summary>쉬는 날: 법정 공휴일(내장 목록에서 채움) + 병원 휴진일. 관리자가 더하거나 지울 수 있습니다.</summary>
public sealed class Holiday
{
    public DateOnly Date { get; set; }
    public required string Name { get; set; }

    /// <summary>내장 공휴일 목록에서 들어온 것인지 (관리자가 추가한 휴진일은 false)</summary>
    public bool BuiltIn { get; set; }
}

/* ===================== 병원 설정 (한 줄) ===================== */

public sealed class ClinicSettings
{
    public int Id { get; set; }

    /// <summary>근무 요일 비트 (일=1, 월=2, 화=4 … 토=64). 기본 월~금.</summary>
    public int WorkDayMask { get; set; } = DefaultWorkDays;
    public TimeOnly WorkStart { get; set; } = new(9, 0);
    public TimeOnly WorkEnd { get; set; } = new(18, 0);

    /// <summary>출퇴근 때 병원에 있는지 확인할지 (병원 네트워크 또는 입구 QR)</summary>
    public bool AttendanceVerify { get; set; }

    /// <summary>병원 공인 IP 또는 대역(CIDR), 줄마다 하나</summary>
    public string AttendanceNetworks { get; set; } = "";

    /// <summary>입구 QR 코드를 만드는 비밀값 (처음 실행 때 무작위 생성)</summary>
    public required string AttendanceSecret { get; set; }

    /// <summary>같은 직무가 같은 날 이 인원 이상 쉬면 달력에 경고</summary>
    public int LeaveWarnSameRole { get; set; } = 2;

    /// <summary>내장 공휴일을 채운 연도 (쉼표 구분). 관리자가 지운 공휴일이 다시 생기지 않게 합니다.</summary>
    public string HolidaySeedYears { get; set; } = "";

    public const int DefaultWorkDays = 0b0111110;
}

/* ===================== 출퇴근 ===================== */

public enum AttendanceMethod
{
    /// <summary>병원 네트워크(허용된 공인 IP)에서</summary>
    Network,
    /// <summary>입구 화면의 QR을 찍어서</summary>
    Qr,
    /// <summary>위치 확인을 끈 상태에서</summary>
    Unverified,
    /// <summary>관리자가 직접 입력·수정</summary>
    Manual
}

/// <summary>직원 한 명의 하루 출퇴근. 직원·날짜(병원 시간대)마다 하나.</summary>
public sealed class AttendanceRecord
{
    public int Id { get; set; }
    public int StaffId { get; set; }
    public Staff Staff { get; set; } = null!;
    public DateOnly Date { get; set; }
    public DateTime? CheckInAt { get; set; }
    public AttendanceMethod? CheckInMethod { get; set; }
    public DateTime? CheckOutAt { get; set; }
    public AttendanceMethod? CheckOutMethod { get; set; }

    /// <summary>관리자가 고친 경우: 누가·언제·왜</summary>
    public DateTime? EditedAt { get; set; }
    public string? EditedByName { get; set; }
    public string? EditReason { get; set; }
}

/* ===================== 직원 주차 ===================== */

/// <summary>직원 차량. 번호는 공백·하이픈을 뺀 형태(12가3456)로 저장합니다.</summary>
public sealed class Vehicle
{
    public int Id { get; set; }
    public int StaffId { get; set; }
    public Staff Staff { get; set; } = null!;
    public required string Plate { get; set; }

    /// <summary>차종·색 (예: 흰색 아반떼) — 이중 주차 연락 때 찾기 쉽게</summary>
    public string? Model { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>주차면 (예: B1-03)</summary>
public sealed class ParkingSpot
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Note { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>주차면 배정 기간. 끝 날짜가 비어 있으면 계속 사용.</summary>
public sealed class ParkingAssignment
{
    public int Id { get; set; }
    public int SpotId { get; set; }
    public ParkingSpot Spot { get; set; } = null!;
    public int StaffId { get; set; }
    public Staff Staff { get; set; } = null!;
    public int? VehicleId { get; set; }
    public Vehicle? Vehicle { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public required string AssignedByName { get; set; }
}

public enum ParkingRequestStatus
{
    Pending,
    Assigned,
    Rejected,
    Cancelled
}

/// <summary>주차 배정 신청 (대기 순서대로 관리자가 배정)</summary>
public sealed class ParkingRequest
{
    public int Id { get; set; }
    public int StaffId { get; set; }
    public Staff Staff { get; set; } = null!;
    public int? VehicleId { get; set; }
    public Vehicle? Vehicle { get; set; }
    public string? Note { get; set; }
    public ParkingRequestStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecidedByName { get; set; }
    public string? DecisionNote { get; set; }
}
