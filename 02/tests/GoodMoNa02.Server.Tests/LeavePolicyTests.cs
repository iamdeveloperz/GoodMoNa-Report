using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Intranet;

namespace GoodMoNa02.Server.Tests;

/// <summary>근로기준법 제60조 연차 발생 규칙 (입사일 기준) — 순수 계산</summary>
public sealed class LeavePolicyTests
{
    private static DateOnly D(string iso) => DateOnly.Parse(iso);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 15)]
    [InlineData(2, 15)]
    [InlineData(3, 16)]  // 3년 이상: 최초 1년 넘는 근속 2년마다 1일
    [InlineData(4, 16)]
    [InlineData(5, 17)]
    [InlineData(10, 19)]
    [InlineData(21, 25)] // 최대 25일
    [InlineData(30, 25)]
    public void 근속연수별_연차는_15일에서_2년마다_1일씩_최대_25일(int years, int days) =>
        Assert.Equal(days, LeavePolicy.AnnualDays(years));

    [Theory]
    [InlineData("2026-03-02", "2026-03-01", -1)]
    [InlineData("2026-03-02", "2026-03-02", 0)]
    [InlineData("2026-03-02", "2027-03-01", 0)]
    [InlineData("2026-03-02", "2027-03-02", 1)]
    [InlineData("2024-02-29", "2025-02-28", 1)] // 윤일 입사: 평년에는 2/28이 기념일
    public void 만_근속연수(string hire, string on, int years) =>
        Assert.Equal(years, LeavePolicy.ServiceYears(D(hire), D(on)));

    [Theory]
    [InlineData("2026-03-15", 0)] // 입사일
    [InlineData("2026-04-14", 0)] // 아직 한 달이 안 됨
    [InlineData("2026-04-15", 1)] // 1개월 개근
    [InlineData("2026-09-15", 6)]
    [InlineData("2027-02-15", 11)]
    [InlineData("2027-03-14", 11)] // 1년 미만은 최대 11일
    public void 입사_1년_미만은_한_달마다_1일(string asOf, int expected)
    {
        var hire = D("2026-03-15");
        var period = LeavePolicy.PeriodOf(hire, hire)!.Value;
        Assert.Equal(expected, LeavePolicy.Granted(hire, period, D(asOf)));
    }

    [Fact]
    public void 입사_1년이_되는_날_새_기간이_시작되고_15일이_생긴다()
    {
        var hire = D("2026-03-15");
        var p = LeavePolicy.PeriodOf(hire, D("2027-03-15"))!.Value;
        Assert.Equal(D("2027-03-15"), p.Start);
        Assert.Equal(D("2028-03-14"), p.LastDay);
        Assert.Equal(15, LeavePolicy.Granted(hire, p, D("2027-03-15")));

        // 1년 미만 기간의 월차는 그 전날까지만 (새 기간에 넘어오지 않음)
        var first = LeavePolicy.PeriodOf(hire, D("2027-03-14"))!.Value;
        Assert.Equal(D("2027-03-14"), first.LastDay);
    }

    [Fact]
    public void 다음_발생일_안내()
    {
        Assert.Equal((D("2026-12-15"), 1), LeavePolicy.NextGrant(D("2026-03-15"), D("2026-11-20")));
        Assert.Equal((D("2027-03-15"), 15), LeavePolicy.NextGrant(D("2026-03-15"), D("2027-02-20")));
        Assert.Equal((D("2027-03-02"), 16), LeavePolicy.NextGrant(D("2024-03-02"), D("2026-11-16"))); // 3년차 16일
    }

    [Fact]
    public void 근무일만_세고_반차는_반일()
    {
        var holidays = new Dictionary<DateOnly, string> { [D("2026-12-25")] = "성탄절" };
        var cal = new WorkCalendar(ClinicSettings.DefaultWorkDays, holidays);
        var r = new LeaveRequest { Portion = LeavePortion.Full, StartDate = D("2026-12-24"), EndDate = D("2026-12-28") }; // 목~월
        Assert.Equal(2m, cal.Days(r)); // 목·월 (금은 성탄절, 토·일 제외)
        Assert.Equal(0.5m, cal.Days(new LeaveRequest { Portion = LeavePortion.AmHalf, StartDate = D("2026-12-24"), EndDate = D("2026-12-24") }));

        var sixDays = new WorkCalendar(WorkCalendar.MaskOf([1, 2, 3, 4, 5, 6]), holidays);
        Assert.Equal(3m, sixDays.Days(r)); // 토요일 근무면 토요일도 셈
    }

    [Fact]
    public void 내장_공휴일_목록은_날짜가_겹치지_않는다() =>
        Assert.Equal(KoreanHolidays.All.Count, KoreanHolidays.All.Select(h => h.Date).Distinct().Count());
}
