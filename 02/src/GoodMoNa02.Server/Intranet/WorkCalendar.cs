using GoodMoNa02.Server.Data;

namespace GoodMoNa02.Server.Intranet;

/// <summary>근무일 판단: 근무 요일이고, 공휴일·휴진일이 아닌 날. 휴가 일수는 근무일만 셉니다.</summary>
public sealed class WorkCalendar(int workDayMask, IReadOnlyDictionary<DateOnly, string> holidays)
{
    public int WorkDayMask { get; } = workDayMask;
    public IReadOnlyDictionary<DateOnly, string> Holidays { get; } = holidays;

    public static bool IsWorkWeekday(int mask, DayOfWeek day) => (mask & (1 << (int)day)) != 0;

    public bool IsWorkday(DateOnly d) => IsWorkWeekday(WorkDayMask, d.DayOfWeek) && !Holidays.ContainsKey(d);

    public string? HolidayName(DateOnly d) => Holidays.GetValueOrDefault(d);

    public static IEnumerable<DateOnly> Dates(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1)) yield return d;
    }

    /// <summary>휴가 한 건이 그날 차지하는 일수 (근무일이 아니면 0, 반차는 0.5)</summary>
    public decimal DayValue(LeavePortion portion, DateOnly d) =>
        !IsWorkday(d) ? 0m : portion == LeavePortion.Full ? 1m : 0.5m;

    /// <summary>휴가 한 건의 일수. <paramref name="within"/>를 주면 그 기간 안의 날만 셉니다.</summary>
    public decimal Days(LeaveRequest r, LeavePeriod? within = null) =>
        Dates(r.StartDate, r.EndDate).Where(d => within is null || within.Value.Contains(d)).Sum(d => DayValue(r.Portion, d));

    public static int MaskOf(IEnumerable<int> weekdays) => weekdays.Where(d => d is >= 0 and <= 6).Aggregate(0, (m, d) => m | (1 << d));

    public static int[] WeekdaysOf(int mask) => Enumerable.Range(0, 7).Where(d => (mask & (1 << d)) != 0).ToArray();
}
