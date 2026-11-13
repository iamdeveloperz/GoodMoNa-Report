using GoodMoNa02.Server.Data;

namespace GoodMoNa02.Server.Contracts;

/// <summary>직무는 문자열로 받아 직접 검사합니다(잘못된 값이면 한국어 오류를 돌려주려고).</summary>
public sealed record SignupRequest(string? LoginId, string? Name, string? Password, string? RequestedRole, string? Department, string? Phone);

public sealed record BootstrapRequest(string? LoginId, string? Name, string? Password, string? Role);

public sealed record LoginRequest(string? LoginId, string? Password);

public sealed record MeDto(int Id, string LoginId, string Name, StaffRole Role);

public sealed record SignupResultDto(string LoginId, StaffStatus Status);

public sealed record BootstrapStatusDto(bool Available);

public sealed record StaffDto(
    int Id,
    string LoginId,
    string Name,
    StaffRole Role,
    StaffRole RequestedRole,
    StaffStatus Status,
    string? Department,
    string? Phone,
    DateTime CreatedAt,
    DateTime? DecidedAt,
    string? DecidedBy,
    string? RejectReason);

public sealed record ApproveRequest(string? Role);

public sealed record RejectRequest(string? Reason);

public sealed record ChangeRoleRequest(string? Role);
