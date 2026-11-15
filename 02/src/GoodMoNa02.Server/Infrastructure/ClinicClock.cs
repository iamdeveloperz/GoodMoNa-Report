using Microsoft.Extensions.Options;

namespace GoodMoNa02.Server.Infrastructure;

/// <summary>"오늘"은 서버 시간대가 아니라 병원 시간대(기본 Asia/Seoul) 기준입니다.</summary>
public sealed class ClinicClock(TimeProvider time, IOptions<AppOptions> options)
{
    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public DateOnly Today => DateOnly.FromDateTime(ToLocal(UtcNow));

    /// <summary>UTC 시각 → 병원 현지 시각</summary>
    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);

    /// <summary>병원 현지 날짜·시각 → UTC</summary>
    public DateTime ToUtc(DateOnly date, TimeOnly at) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date.ToDateTime(at), DateTimeKind.Unspecified), _zone);
}
