using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa02.Server.Intranet;

/// <summary>직원 한 명의 연차 잔여: 발생(법정) + 조정 − 사용(승인) − 대기(승인 전 신청).</summary>
public static class LeaveLedger
{
    /// <param name="on">어느 연차 기간을 볼지 (그날이 속한 기간)</param>
    /// <param name="asOf">발생 기준일. 1년 미만은 이날까지 생긴 만큼만 (화면은 오늘, 신청 검사는 휴가 마지막 날)</param>
    /// <param name="today">다음 발생일 안내 기준</param>
    /// <param name="excludeRequestId">승인 검사 때 그 신청 자신은 대기에서 뺍니다</param>
    public static async Task<LeaveBalanceDto?> BalanceAsync(
        AppDbContext db, int staffId, DateOnly? hireDate, DateOnly on, DateOnly asOf, DateOnly today, CancellationToken ct, int? excludeRequestId = null)
    {
        if (hireDate is not { } hire || LeavePolicy.PeriodOf(hire, on) is not { } p) return null;

        var cal = await ClinicSetup.CalendarAsync(db, p.Start, p.LastDay, ct);
        var requests = await db.LeaveRequests.AsNoTracking()
            .Where(r => r.StaffId == staffId && r.Type == LeaveType.Annual
                        && (r.Status == LeaveStatus.Approved || r.Status == LeaveStatus.Pending)
                        && r.EndDate >= p.Start && r.StartDate < p.End && r.Id != excludeRequestId)
            .ToListAsync(ct);
        var used = requests.Where(r => r.Status == LeaveStatus.Approved).Sum(r => cal.Days(r, p));
        var pending = requests.Where(r => r.Status == LeaveStatus.Pending).Sum(r => cal.Days(r, p));
        var adjusted = (await db.LeaveAdjustments.AsNoTracking()
            .Where(a => a.StaffId == staffId && a.EffectiveDate >= p.Start && a.EffectiveDate < p.End)
            .Select(a => a.Days).ToListAsync(ct)).Sum();

        var granted = LeavePolicy.Granted(hire, p, asOf);
        var remaining = granted + adjusted - used;
        var (nextDate, nextDays) = LeavePolicy.NextGrant(hire, today);
        return new LeaveBalanceDto(p.Start, p.LastDay, p.ServiceYears, granted, adjusted, used, pending, remaining, remaining - pending, nextDate, nextDays);
    }
}
