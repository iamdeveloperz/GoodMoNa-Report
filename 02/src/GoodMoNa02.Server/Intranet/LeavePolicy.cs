namespace GoodMoNa02.Server.Intranet;

/// <summary>연차 기간: 입사 기념일부터 다음 기념일 전날까지. <see cref="End"/>는 포함하지 않습니다.</summary>
public readonly record struct LeavePeriod(DateOnly Start, DateOnly End, int ServiceYears)
{
    public bool Contains(DateOnly d) => d >= Start && d < End;

    /// <summary>이 기간의 연차를 쓸 수 있는 마지막 날</summary>
    public DateOnly LastDay => End.AddDays(-1);
}

/// <summary>
/// 근로기준법 제60조에 따른 연차 발생 (입사일 기준).
/// · 입사 1년 미만: 1개월 개근마다 1일 (최대 11일), 입사 1년이 되는 날까지 사용 (§60②, 2020년 개정 반영)
/// · 1년 이상: 15일 (§60①)
/// · 3년 이상: 최초 1년을 넘는 근속 2년마다 1일 가산, 최대 25일 (§60④)
/// 출근율 80% 요건은 출퇴근 기록과 별개로 "충족"으로 보고, 아닌 경우는 관리자가 수동 조정합니다.
/// 상태가 없는 순수 함수라 테스트에서 날짜만 바꿔 경계 사례를 검증합니다.
/// </summary>
public static class LeavePolicy
{
    public const int FirstYearMax = 11;
    public const int AnnualBase = 15;
    public const int AnnualMax = 25;

    /// <summary>만 근속 연수. 입사 전이면 -1.</summary>
    public static int ServiceYears(DateOnly hire, DateOnly on)
    {
        if (on < hire) return -1;
        var years = on.Year - hire.Year;
        if (hire.AddYears(years) > on) years--;
        return years;
    }

    /// <summary>그날이 속한 연차 기간. 입사 전이면 null.</summary>
    public static LeavePeriod? PeriodOf(DateOnly hire, DateOnly on)
    {
        var n = ServiceYears(hire, on);
        if (n < 0) return null;
        return new LeavePeriod(hire.AddYears(n), hire.AddYears(n + 1), n);
    }

    /// <summary>근속 n년(1 이상)이 되는 날 생기는 연차 일수</summary>
    public static int AnnualDays(int serviceYears) =>
        serviceYears < 1 ? 0 : Math.Min(AnnualMax, AnnualBase + (serviceYears - 1) / 2);

    /// <summary>
    /// 기준일(asOf)까지 그 기간에 생긴 연차. 1년 미만 기간은 매월 입사일과 같은 날에 1일씩 늘고,
    /// 1년 이상 기간은 기간 첫날에 한 번에 생깁니다. 기준일이 기간 시작 전이면 0.
    /// </summary>
    public static int Granted(DateOnly hire, LeavePeriod period, DateOnly asOf)
    {
        if (asOf < period.Start) return 0;
        if (period.ServiceYears >= 1) return AnnualDays(period.ServiceYears);

        var months = 0;
        for (var k = 1; k <= FirstYearMax; k++)
        {
            var on = hire.AddMonths(k);
            if (on > asOf || on >= period.End) break;
            months = k;
        }
        return months;
    }

    /// <summary>다음에 연차가 새로 생기는 날과 그때 늘어나는 일수 (화면 안내용)</summary>
    public static (DateOnly Date, int Days) NextGrant(DateOnly hire, DateOnly today)
    {
        var period = PeriodOf(hire, today) ?? new LeavePeriod(hire, hire.AddYears(1), 0);
        if (today < hire) return (hire.AddMonths(1), 1);
        if (period.ServiceYears == 0)
        {
            for (var k = 1; k <= FirstYearMax; k++)
                if (hire.AddMonths(k) > today) return (hire.AddMonths(k), 1);
        }
        return (period.End, AnnualDays(period.ServiceYears + 1));
    }
}
