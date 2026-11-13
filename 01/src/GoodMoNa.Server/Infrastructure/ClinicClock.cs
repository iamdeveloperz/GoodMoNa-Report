using Microsoft.Extensions.Options;

namespace GoodMoNa.Server.Infrastructure;

/// <summary>"오늘"은 서버 시간대가 아니라 병원 시간대(기본 Asia/Seoul) 기준입니다.</summary>
public sealed class ClinicClock(TimeProvider time, IOptions<AppOptions> options)
{
    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, _zone));

    /// <summary>병원 시간대의 날짜·시각을 UTC로 (이전 기록의 방문 시각을 정할 때)</summary>
    public DateTime ToUtc(DateOnly date, TimeOnly time) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), _zone);
}
