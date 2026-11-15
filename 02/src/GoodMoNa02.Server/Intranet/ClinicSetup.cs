using System.Net;
using System.Security.Cryptography;
using GoodMoNa02.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa02.Server.Intranet;

/// <summary>병원 설정(한 줄)과 휴무일 표를 준비하고, 근무일 달력을 만들어 줍니다.</summary>
public static class ClinicSetup
{
    /// <summary>서버 시작 때: 설정 줄이 없으면 만들고, 아직 채우지 않은 연도의 내장 공휴일을 채웁니다.</summary>
    public static async Task EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        var s = await db.Settings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (s is null)
        {
            s = new ClinicSettings { Id = 1, AttendanceSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
            db.Settings.Add(s);
        }

        var seeded = s.HolidaySeedYears.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        foreach (var year in KoreanHolidays.Years.Where(y => !seeded.Contains(y.ToString())))
        {
            var dates = KoreanHolidays.All.Where(h => h.Date.Year == year).ToList();
            var existing = await db.Holidays.Where(h => h.Date.Year == year).Select(h => h.Date).ToListAsync(ct);
            foreach (var (date, name) in dates.Where(d => !existing.Contains(d.Date)))
                db.Holidays.Add(new Holiday { Date = date, Name = name, BuiltIn = true });
            seeded.Add(year.ToString());
        }
        s.HolidaySeedYears = string.Join(",", seeded.Order());
        await db.SaveChangesAsync(ct);
    }

    public static async Task<ClinicSettings> SettingsAsync(AppDbContext db, CancellationToken ct) =>
        await db.Settings.OrderBy(s => s.Id).FirstOrDefaultAsync(ct) ?? throw new InvalidOperationException("병원 설정이 없습니다. 서버를 다시 시작해 주세요.");

    /// <summary>기간의 근무일 달력 (휴무일은 그 기간 것만 읽음)</summary>
    public static async Task<WorkCalendar> CalendarAsync(AppDbContext db, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var s = await SettingsAsync(db, ct);
        var holidays = await db.Holidays.AsNoTracking().Where(h => h.Date >= from && h.Date <= to).ToDictionaryAsync(h => h.Date, h => h.Name, ct);
        return new WorkCalendar(s.WorkDayMask, holidays);
    }

    /// <summary>줄마다 하나씩 적은 IP·대역(CIDR) 목록을 검사합니다. 잘못된 줄이 있으면 그 줄을 돌려줍니다.</summary>
    public static string? FindInvalidNetwork(IEnumerable<string> lines) =>
        lines.FirstOrDefault(l => !IPAddress.TryParse(l, out _) && !IPNetwork.TryParse(l, out _));

    public static string[] Networks(ClinicSettings s) =>
        s.AttendanceNetworks.Split(['\n', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>요청한 기기의 IP가 병원 네트워크 목록에 있는지</summary>
    public static bool IsClinicNetwork(ClinicSettings s, IPAddress? ip)
    {
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        foreach (var n in Networks(s))
        {
            if (IPAddress.TryParse(n, out var one) && one.Equals(ip)) return true;
            if (IPNetwork.TryParse(n, out var net) && net.Contains(ip)) return true;
        }
        return false;
    }

    /// <summary>
    /// 접속한 기기의 공인 IP. Cloudflare를 거치면 Cf-Connecting-IP에 원래 IP가 있고,
    /// 서버 PC에서 바로 열면 연결 IP(보통 127.0.0.1)입니다.
    /// </summary>
    public static IPAddress? ClientIp(HttpContext http) =>
        http.Request.Headers["Cf-Connecting-IP"].FirstOrDefault() is { } cf && IPAddress.TryParse(cf, out var ip) ? ip : http.Connection.RemoteIpAddress;
}
