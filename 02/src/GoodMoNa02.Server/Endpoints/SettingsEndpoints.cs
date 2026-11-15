using GoodMoNa02.Server.Auth;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Intranet;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa02.Server.Endpoints;

/// <summary>병원 설정(근무 요일·시간, 출퇴근 확인, 휴가 경고 인원)과 휴무일. 휴무일 목록은 모두 볼 수 있고 바꾸기는 대표원장·관리자만.</summary>
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this RouteGroupBuilder intranet)
    {
        intranet.MapGet("/settings", Get).RequireAuthorization(Policies.ManageStaff);
        intranet.MapPut("/settings", Update).RequireAuthorization(Policies.ManageStaff);
        intranet.MapGet("/holidays", Holidays);
        intranet.MapPost("/holidays", AddHoliday).RequireAuthorization(Policies.ManageStaff);
        intranet.MapDelete("/holidays/{date}", RemoveHoliday).RequireAuthorization(Policies.ManageStaff);
    }

    private static SettingsDto ToDto(ClinicSettings s, HttpContext http) =>
        new(WorkCalendar.WeekdaysOf(s.WorkDayMask), s.WorkStart, s.WorkEnd, s.AttendanceVerify, ClinicSetup.Networks(s), s.LeaveWarnSameRole,
            ClinicSetup.ClientIp(http)?.ToString());

    private static async Task<IResult> Get(HttpContext http, AppDbContext db, CancellationToken ct) =>
        Results.Ok(ToDto(await ClinicSetup.SettingsAsync(db, ct), http));

    private static async Task<IResult> Update(SettingsRequest req, HttpContext http, AppDbContext db, CancellationToken ct)
    {
        var s = await db.Settings.OrderBy(x => x.Id).FirstAsync(ct);
        if (req.WorkDays is not null)
        {
            var mask = WorkCalendar.MaskOf(req.WorkDays);
            if (mask == 0) return Problems.Validation("workDays", "근무 요일을 하루 이상 골라 주세요.");
            s.WorkDayMask = mask;
        }
        if (req.WorkStart is { } start) s.WorkStart = start;
        if (req.WorkEnd is { } end) s.WorkEnd = end;
        if (s.WorkEnd <= s.WorkStart) return Problems.Validation("workEnd", "퇴근 시각은 출근 시각보다 늦어야 합니다.");
        if (req.LeaveWarnSameRole is { } warn)
        {
            if (warn is < 1 or > 20) return Problems.Validation("leaveWarnSameRole", "경고 인원은 1~20명 사이로 정해 주세요.");
            s.LeaveWarnSameRole = warn;
        }
        if (req.AttendanceNetworks is not null)
        {
            var lines = req.AttendanceNetworks.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToArray();
            if (ClinicSetup.FindInvalidNetwork(lines) is { } bad) return Problems.Validation("attendanceNetworks", $"IP 형식이 아닙니다: {bad} (예: 203.0.113.7 또는 203.0.113.0/24)");
            s.AttendanceNetworks = string.Join('\n', lines);
        }
        // 확인을 켜고 네트워크를 비워 두면 입구 QR로만 확인합니다(막지 않고 화면에서 안내).
        s.AttendanceVerify = req.AttendanceVerify;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(s, http));
    }

    private static async Task<IResult> Holidays(int? year, AppDbContext db, Infrastructure.ClinicClock clock, CancellationToken ct)
    {
        var y = year ?? clock.Today.Year;
        return Results.Ok(await db.Holidays.AsNoTracking().Where(h => h.Date >= new DateOnly(y, 1, 1) && h.Date <= new DateOnly(y, 12, 31))
            .OrderBy(h => h.Date).Select(h => new HolidayDto(h.Date, h.Name, h.BuiltIn)).ToListAsync(ct));
    }

    private static async Task<IResult> AddHoliday(NewHolidayRequest req, AppDbContext db, CancellationToken ct)
    {
        if (req.Date is not { } date) return Problems.Validation("date", "날짜를 골라 주세요.");
        var name = StaffInput.Optional(req.Name, 40);
        if (name is null) return Problems.Validation("name", "이름을 적어 주세요. (예: 병원 휴진, 임시공휴일)");
        if (await db.Holidays.AnyAsync(h => h.Date == date, ct)) return Problems.Conflict("이미 쉬는 날로 등록된 날짜입니다.");
        db.Holidays.Add(new Holiday { Date = date, Name = name, BuiltIn = false });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/intranet/holidays?year={date.Year}", new HolidayDto(date, name, false));
    }

    private static async Task<IResult> RemoveHoliday(string date, AppDbContext db, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var d)) return Problems.Validation("date", "날짜 형식이 올바르지 않습니다.");
        await db.Holidays.Where(h => h.Date == d).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }
}
