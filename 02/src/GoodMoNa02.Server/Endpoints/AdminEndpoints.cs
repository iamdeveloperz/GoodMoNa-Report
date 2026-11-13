using System.Security.Claims;
using GoodMoNa02.Server.Auth;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa02.Server.Endpoints;

/// <summary>직원 승인·반려·직무 변경·사용 중지. 대표원장과 관리자만 씁니다.</summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/staff").RequireAuthorization(Policies.ManageStaff);
        g.MapGet("/", List);
        g.MapPost("/{id:int}/approve", Approve);
        g.MapPost("/{id:int}/reject", Reject);
        g.MapPut("/{id:int}/role", ChangeRole);
        g.MapPost("/{id:int}/disable", Disable);
        g.MapPost("/{id:int}/enable", Enable);
    }

    private static async Task<IResult> List(string? status, AppDbContext db, CancellationToken ct)
    {
        var q = db.Staff.AsNoTracking();
        if (status is not null)
        {
            if (!Enum.TryParse<StaffStatus>(status, true, out var st)) return Problems.Validation("status", "상태 값이 올바르지 않습니다.");
            q = q.Where(s => s.Status == st);
        }
        var list = await q.OrderBy(s => s.Status == StaffStatus.Pending ? 0 : 1).ThenByDescending(s => s.CreatedAt).ToListAsync(ct);
        var deciderIds = list.Where(s => s.DecidedById is not null).Select(s => s.DecidedById!.Value).Distinct().ToList();
        var names = await db.Staff.AsNoTracking().Where(s => deciderIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return Results.Ok(list.Select(s => ToDto(s, s.DecidedById is { } d ? names.GetValueOrDefault(d) : null)));
    }

    private static async Task<IResult> Approve(int id, ApproveRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var staff = await db.Staff.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (staff is null) return Problems.NotFound("직원을 찾을 수 없습니다.");
        if (staff.Status is not (StaffStatus.Pending or StaffStatus.Rejected)) return Problems.Conflict("승인 대기 중이거나 반려된 신청만 승인할 수 있습니다.");

        var role = staff.RequestedRole;
        if (req.Role is not null && !StaffInput.TryParseRole(req.Role, out role)) return Problems.Validation("role", "직무 값이 올바르지 않습니다.");

        staff.Status = StaffStatus.Active;
        staff.Role = role;
        staff.RejectReason = null;
        Decide(staff, user, clock);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(staff, user.Identity?.Name));
    }

    private static async Task<IResult> Reject(int id, RejectRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var staff = await db.Staff.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (staff is null) return Problems.NotFound("직원을 찾을 수 없습니다.");
        if (staff.Status != StaffStatus.Pending) return Problems.Conflict("승인 대기 중인 신청만 반려할 수 있습니다.");

        staff.Status = StaffStatus.Rejected;
        staff.RejectReason = StaffInput.Optional(req.Reason, 200);
        Decide(staff, user, clock);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(staff, user.Identity?.Name));
    }

    /// <summary>직무 변경은 다음 출입증 갱신(최대 15분) 때 반영됩니다.</summary>
    private static async Task<IResult> ChangeRole(int id, ChangeRoleRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        if (!StaffInput.TryParseRole(req.Role, out var role)) return Problems.Validation("role", "직무 값이 올바르지 않습니다.");
        var staff = await db.Staff.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (staff is null) return Problems.NotFound("직원을 찾을 수 없습니다.");
        if (staff.Status != StaffStatus.Active) return Problems.Conflict("사용 중인 직원만 직무를 바꿀 수 있습니다.");
        if (IsManager(staff.Role) && !IsManager(role) && await IsLastManagerAsync(db, staff.Id, ct))
            return Problems.Conflict("마지막 대표원장·관리자의 직무는 바꿀 수 없습니다. 다른 관리자를 먼저 승인해 주세요.");

        staff.Role = role;
        Decide(staff, user, clock);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(staff, user.Identity?.Name));
    }

    /// <summary>퇴사·사용 중지. 로그인 세션을 모두 끊어, 늦어도 출입증 유효 시간 안에 모든 서버에서 막힙니다.</summary>
    private static async Task<IResult> Disable(int id, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var staff = await db.Staff.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (staff is null) return Problems.NotFound("직원을 찾을 수 없습니다.");
        if (staff.Status != StaffStatus.Active) return Problems.Conflict("사용 중인 직원만 중지할 수 있습니다.");
        if (staff.Id == AuthEndpoints.StaffId(user)) return Problems.Conflict("자기 계정은 중지할 수 없습니다.");
        if (IsManager(staff.Role) && await IsLastManagerAsync(db, staff.Id, ct))
            return Problems.Conflict("마지막 대표원장·관리자는 중지할 수 없습니다.");

        staff.Status = StaffStatus.Disabled;
        Decide(staff, user, clock);
        await db.SaveChangesAsync(ct);
        await AuthEndpoints.RevokeAllAsync(db, staff.Id, clock.UtcNow, ct);
        return Results.Ok(ToDto(staff, user.Identity?.Name));
    }

    private static async Task<IResult> Enable(int id, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var staff = await db.Staff.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (staff is null) return Problems.NotFound("직원을 찾을 수 없습니다.");
        if (staff.Status != StaffStatus.Disabled) return Problems.Conflict("중지된 직원만 다시 사용하게 할 수 있습니다.");

        staff.Status = StaffStatus.Active;
        Decide(staff, user, clock);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(staff, user.Identity?.Name));
    }

    private static bool IsManager(StaffRole role) => role is StaffRole.Owner or StaffRole.Admin;

    private static Task<bool> IsLastManagerAsync(AppDbContext db, int staffId, CancellationToken ct) =>
        db.Staff.AllAsync(s => s.Id == staffId || s.Status != StaffStatus.Active || (s.Role != StaffRole.Owner && s.Role != StaffRole.Admin), ct);

    private static void Decide(Staff staff, ClaimsPrincipal user, ClinicClock clock)
    {
        staff.DecidedAt = clock.UtcNow;
        staff.DecidedById = AuthEndpoints.StaffId(user);
    }

    private static StaffDto ToDto(Staff s, string? decidedBy) => new(
        s.Id, s.LoginId, s.Name, s.Role, s.RequestedRole, s.Status, s.Department, s.Phone, s.CreatedAt, s.DecidedAt, decidedBy, s.RejectReason);
}
