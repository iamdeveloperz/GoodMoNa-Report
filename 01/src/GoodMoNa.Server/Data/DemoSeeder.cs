using System.Globalization;
using System.Text;
using GoodMoNa.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa.Server.Data;

/// <summary>
/// 시연용 예시 데이터. DB가 비어 있고 App:SeedDemoData가 켜져 있을 때만 들어갑니다.
/// 과거 방문 사진은 두피 근접 사진을 흉내 낸 SVG로 만듭니다 (실제 환자 사진 아님).
/// </summary>
public static class DemoSeeder
{
    private sealed record PastVisit(int DaysAgo, Progress Progress);
    private sealed record Sample(string Name, string Birth, double Density, PastVisit[] Visits);

    private static readonly Sample[] Samples =
    [
        new("김민수", "1988-04-17", .50, [new(42, Progress.Unchanged), new(21, Progress.Improved)]),
        new("김민수", "1995-11-03", .72, [new(14, Progress.Unchanged)]),
        new("김민지", "1990-02-14", .64, [new(21, Progress.Improved)]),
        new("이서연", "1992-07-08", .58, [new(21, Progress.Unchanged)]),
        new("박지훈", "1979-01-25", .42, [new(63, Progress.Worsened), new(42, Progress.Unchanged), new(21, Progress.Improved)]),
        new("최유진", "2001-09-30", .66, [])
    ];

    public static async Task SeedAsync(AppDbContext db, DataPaths paths, ClinicClock clock, CancellationToken ct = default)
    {
        if (await db.Patients.AnyAsync(ct)) return;

        var today = clock.Today;
        var now = clock.UtcNow;
        var seed = 7;

        foreach (var s in Samples)
        {
            var patient = new Patient { Name = s.Name, BirthDate = DateOnly.Parse(s.Birth, CultureInfo.InvariantCulture), CreatedAt = now };
            for (var i = 0; i < s.Visits.Length; i++)
            {
                var pv = s.Visits[i];
                var date = today.AddDays(-pv.DaysAgo);
                var at = DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(2, 0)), DateTimeKind.Utc); // 오전 11시(KST)쯤

                // 방문이 쌓일수록 조금씩 빽빽해지도록
                var density = s.Density - (s.Visits.Length - 1 - i) * .04;
                var relative = $"photos/demo/{Guid.NewGuid():N}.svg";
                var full = paths.Resolve(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var svg = ScalpSvg(++seed * 97, density);
                await File.WriteAllTextAsync(full, svg, ct);

                patient.Visits.Add(new Visit
                {
                    VisitDate = date,
                    Status = VisitStatus.Reported,
                    Progress = pv.Progress,
                    CreatedAt = at,
                    ReportedAt = at.AddMinutes(4),
                    ReportedBy = "간호사",
                    Photos = [new Photo { StoragePath = relative, ContentType = "image/svg+xml", SizeBytes = Encoding.UTF8.GetByteCount(svg), UploadedAt = at }]
                });
            }
            db.Patients.Add(patient);
        }

        await db.SaveChangesAsync(ct);
    }

    private static string ScalpSvg(int seed, double density)
    {
        var r = new Random(seed);
        var sb = new StringBuilder(200_000);
        string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        sb.Append("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 480 640' width='480' height='640'>");
        sb.Append("<defs><radialGradient id='s' cx='50%' cy='47%' r='70%'><stop offset='0' stop-color='#ebc6a8'/><stop offset='1' stop-color='#b98a6c'/></radialGradient>");
        sb.Append("<radialGradient id='v' cx='50%' cy='50%' r='75%'><stop offset='.55' stop-color='#000' stop-opacity='0'/><stop offset='1' stop-color='#000' stop-opacity='.38'/></radialGradient></defs>");
        sb.Append("<rect width='480' height='640' fill='url(#s)'/><g fill='none' stroke-linecap='round'>");

        double cx = 240 + (r.NextDouble() - .5) * 40, cy = 300 + (r.NextDouble() - .5) * 40;
        for (var i = 0; i < 4200; i++)
        {
            double x = r.NextDouble() * 520 - 20, y = r.NextDouble() * 680 - 20;
            double dx = x - cx, dy = y - cy;
            var d = Math.Min(1.2, Math.Sqrt(dx * dx + dy * dy) / 300);
            var keep = Math.Min(.95, density * .45 + d * d * density * 1.1);
            if (r.NextDouble() > keep) continue;

            var ang = Math.Atan2(dy, dx) + Math.PI * .42 + (r.NextDouble() - .5) * .7;
            var len = 14 + r.NextDouble() * 20;
            double mx = x + Math.Cos(ang) * len * .5 + (r.NextDouble() - .5) * 5, my = y + Math.Sin(ang) * len * .5 + (r.NextDouble() - .5) * 5;
            double ex = x + Math.Cos(ang) * len, ey = y + Math.Sin(ang) * len;
            int cr = 30 + r.Next(22), cg = 22 + r.Next(12), cb = 18 + r.Next(8);

            sb.Append("<path d='M").Append(F(x)).Append(' ').Append(F(y))
              .Append('Q').Append(F(mx)).Append(' ').Append(F(my)).Append(' ').Append(F(ex)).Append(' ').Append(F(ey))
              .Append("' stroke='rgb(").Append(cr).Append(',').Append(cg).Append(',').Append(cb)
              .Append(")' stroke-opacity='").Append(F(.78 + r.NextDouble() * .2))
              .Append("' stroke-width='").Append(F(1.3 + r.NextDouble() * 1.3)).Append("'/>");
        }

        sb.Append("</g><rect width='480' height='640' fill='url(#v)'/></svg>");
        return sb.ToString();
    }
}
