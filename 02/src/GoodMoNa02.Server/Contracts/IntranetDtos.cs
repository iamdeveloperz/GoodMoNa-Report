using GoodMoNa02.Server.Data;

namespace GoodMoNa02.Server.Contracts;

public static class NoticeLimits
{
    public const int Title = 100;
    public const int Body = 5000;
}

/* ---------- 홈 · 주소록 · 내 정보 ---------- */

public sealed record PersonDto(int Id, string Name, StaffRole Role, string? Department, string? Phone, DateOnly? HireDate);

public sealed record ProfileRequest(string? Department, string? Phone);

/// <summary>관리자가 고치는 직원 정보. 입사일은 "" 이면 지움.</summary>
public sealed record StaffProfileRequest(string? Department, string? Phone, string? HireDate);

public sealed record HomeDto(
    DateOnly Today,
    bool Workday,
    string? HolidayName,
    AttendanceDayDto? Attendance,
    LeaveBalanceDto? Leave,
    IReadOnlyList<NoticeListItemDto> RequiredUnread,
    IReadOnlyList<NoticeListItemDto> RecentNotices,
    IReadOnlyList<CalendarLeaveDto> OffToday,
    ManageCountsDto? Manage);

public sealed record ManageCountsDto(int StaffPending, int LeavePending, int ParkingPending, int MissingHireDate);

/* ---------- 공지 ---------- */

public sealed record NoticeRequest(string? Title, string? Body, bool Required);

public sealed record NoticeListItemDto(int Id, string Title, bool Required, string AuthorName, DateTime CreatedAt, DateTime? UpdatedAt, bool Read, int ReadCount, int TargetCount);

public sealed record NoticeDto(int Id, string Title, string Body, bool Required, int AuthorId, string AuthorName, DateTime CreatedAt, DateTime? UpdatedAt, DateTime? ReadAt);

public sealed record NoticeReaderDto(int StaffId, string Name, StaffRole Role, string? Department, DateTime? ReadAt);

public sealed record NoticeReadsDto(IReadOnlyList<NoticeReaderDto> Read, IReadOnlyList<NoticeReaderDto> Unread);

/* ---------- 연차 ---------- */

public sealed record LeaveBalanceDto(
    DateOnly PeriodStart,
    DateOnly PeriodLastDay,
    int ServiceYears,
    decimal Granted,
    decimal Adjusted,
    decimal Used,
    decimal Pending,
    decimal Remaining,
    decimal Available,
    DateOnly NextGrantDate,
    int NextGrantDays);

public sealed record LeaveRequestDto(
    int Id,
    int StaffId,
    string StaffName,
    StaffRole Role,
    string? Department,
    LeaveType Type,
    LeavePortion Portion,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal Days,
    string? Reason,
    LeaveStatus Status,
    DateTime CreatedAt,
    DateTime? DecidedAt,
    string? DecidedByName,
    string? DecisionNote);

public sealed record MyLeaveDto(DateOnly? HireDate, LeaveBalanceDto? Balance, IReadOnlyList<LeaveRequestDto> Requests, IReadOnlyList<LeaveAdjustmentDto> Adjustments);

public sealed record NewLeaveRequest(string? Type, string? Portion, DateOnly? StartDate, DateOnly? EndDate, string? Reason);

public sealed record DecisionRequest(string? Note);

/// <summary>관리자 승인 대기 목록의 한 줄: 신청 + 신청자의 그 기간 잔여</summary>
public sealed record PendingLeaveDto(LeaveRequestDto Request, LeaveBalanceDto? Balance);

public sealed record CalendarLeaveDto(int Id, int StaffId, string Name, StaffRole Role, LeaveType Type, LeavePortion Portion, DateOnly StartDate, DateOnly EndDate, LeaveStatus Status);

public sealed record HolidayDto(DateOnly Date, string Name, bool BuiltIn);

public sealed record LeaveCalendarDto(string Month, int[] WorkDays, IReadOnlyList<HolidayDto> Holidays, IReadOnlyList<CalendarLeaveDto> Items, int WarnSameRole);

public sealed record StaffBalanceDto(int StaffId, string Name, StaffRole Role, string? Department, DateOnly? HireDate, LeaveBalanceDto? Balance);

public sealed record NewAdjustmentRequest(int StaffId, decimal Days, string? Reason, DateOnly? EffectiveDate);

public sealed record LeaveAdjustmentDto(int Id, int StaffId, decimal Days, DateOnly EffectiveDate, string Reason, DateTime CreatedAt, string CreatedByName);

/* ---------- 설정 · 휴무일 ---------- */

public sealed record SettingsDto(int[] WorkDays, TimeOnly WorkStart, TimeOnly WorkEnd, bool AttendanceVerify, string[] AttendanceNetworks, int LeaveWarnSameRole, string? YourIp);

public sealed record SettingsRequest(int[]? WorkDays, TimeOnly? WorkStart, TimeOnly? WorkEnd, bool AttendanceVerify, string[]? AttendanceNetworks, int? LeaveWarnSameRole);

public sealed record NewHolidayRequest(DateOnly? Date, string? Name);

/* ---------- 출퇴근 ---------- */

/// <summary>하루 출퇴근. 상태: Off(근무일 아님) · Leave(휴가) · Working(출근 중) · Done(퇴근) · Absent(기록 없음, 지난 근무일) · None(오늘·앞날 아직 없음)</summary>
public sealed record AttendanceDayDto(
    DateOnly Date,
    bool Workday,
    string? HolidayName,
    DateTime? CheckInAt,
    DateTime? CheckOutAt,
    AttendanceMethod? CheckInMethod,
    AttendanceMethod? CheckOutMethod,
    bool Late,
    bool EarlyLeave,
    LeaveType? LeaveType,
    LeavePortion? LeavePortion,
    string Status,
    string? EditReason,
    string? EditedByName);

public sealed record AttendanceVerifyDto(bool Required, bool OnClinicNetwork, string? Ip);

public sealed record MyAttendanceDto(string Month, TimeOnly WorkStart, TimeOnly WorkEnd, AttendanceVerifyDto Verify, AttendanceDayDto Today, IReadOnlyList<AttendanceDayDto> Days);

public sealed record CheckRequest(string? Code);

public sealed record DailyAttendanceRowDto(int StaffId, string Name, StaffRole Role, string? Department, AttendanceDayDto Day);

public sealed record DailyAttendanceDto(DateOnly Date, bool Workday, string? HolidayName, IReadOnlyList<DailyAttendanceRowDto> Rows);

/// <summary>관리자 수정: 시각은 병원 현지 "HH:mm", 비우면 지움</summary>
public sealed record AttendanceEditRequest(string? CheckIn, string? CheckOut, string? Reason);

public sealed record KioskCodeDto(string Code, string Url, DateTime ExpiresAt, int StepSeconds);

/* ---------- 주차 ---------- */

public sealed record VehicleDto(int Id, int StaffId, string Plate, string? Model);

public sealed record NewVehicleRequest(string? Plate, string? Model);

public sealed record ParkingAssignmentDto(int Id, int SpotId, string SpotName, string? SpotNote, int StaffId, string StaffName, string? Plate, DateOnly StartDate, DateOnly? EndDate, string AssignedByName);

public sealed record ParkingRequestDto(int Id, int StaffId, string StaffName, StaffRole Role, string? Department, int? VehicleId, string? Plate, string? Note, ParkingRequestStatus Status, DateTime CreatedAt, DateTime? DecidedAt, string? DecidedByName, string? DecisionNote);

public sealed record MyParkingDto(IReadOnlyList<VehicleDto> Vehicles, ParkingAssignmentDto? Assignment, ParkingRequestDto? Request);

public sealed record NewParkingRequest(int? VehicleId, string? Note);

public sealed record PlateLookupDto(string Plate, string? Model, string OwnerName, StaffRole Role, string? Department, string? Phone, string? SpotName);

public sealed record SpotDto(int Id, string Name, string? Note, bool Active, ParkingAssignmentDto? Current, ParkingAssignmentDto? Next);

public sealed record SpotRequest(string? Name, string? Note, bool? Active);

public sealed record AssignRequest(int? SpotId, int? StaffId, int? VehicleId, DateOnly? StartDate, DateOnly? EndDate);

public sealed record EndAssignmentRequest(DateOnly? EndDate);
