using System.Globalization;
using System.Security.Claims;
using GoodMoNa03.Server.Auth;
using GoodMoNa03.Server.Contracts;
using GoodMoNa03.Server.Data;
using GoodMoNa03.Server.Infrastructure;
using GoodMoNa03.Server.Media;
using GoodMoNa03.Server.Writing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoodMoNa03.Server.Endpoints;

public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/shorts");
        g.MapGet("/status", Status);
        g.MapPost("/titles/import", ImportTitles);
    }

    /// <summary>ffmpeg·Whisper 모델·AI 설정 상태. 화면 위쪽에 "무엇이 빠졌는지" 안내합니다.</summary>
    private static async Task<IResult> Status(ClaimsPrincipal user, IVideoTool video, IAiWriter ai, AppDbContext db,
        IOptions<AppOptions> options, CancellationToken ct)
    {
        var o = options.Value;
        return Results.Ok(new SystemStatusDto(
            await video.VersionAsync(ct),
            File.Exists(o.Whisper.ModelPath),
            ai.Label,
            ai.Enabled,
            o.MaxUploadMegabytes,
            await db.Titles.CountAsync(ct),
            Policies.ApproverRoles.Any(user.IsInRole)));
    }

    /// <summary>
    /// 기존 쇼츠 제목 가져오기(반복 제목 경고용). tools/collect-shorts.js가 복사해 주는 TSV(순번·영상ID·제목·조회수)를 그대로 붙여 넣거나,
    /// 제목만 한 줄에 하나씩 넣어도 됩니다. 같은 영상ID는 제목·조회수만 갱신합니다.
    /// </summary>
    private static async Task<IResult> ImportTitles(TitleImportRequest req, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var text = req.Text ?? "";
        if (text.Length > 2_000_000) return DraftEndpoints.Problem(413, "목록이 너무 큽니다.");
        var rows = ParseTitles(text);
        if (rows.Count == 0) return DraftEndpoints.Problem(400, "가져올 제목이 없습니다. collect-shorts.js가 복사한 목록을 붙여 넣어 주세요.");

        var existing = await db.Titles.ToListAsync(ct);
        var byId = existing.Where(t => t.VideoId is not null).ToDictionary(t => t.VideoId!, StringComparer.Ordinal);
        var titles = existing.Select(t => t.Title).ToHashSet(StringComparer.Ordinal);
        int added = 0, updated = 0;
        foreach (var (videoId, title, views) in rows)
        {
            if (videoId is not null && byId.TryGetValue(videoId, out var row))
            {
                if (row.Title != title || row.Views != views) updated++;
                row.Title = title;
                row.Views = views;
                continue;
            }
            if (videoId is null && titles.Contains(title)) continue;
            var t = new TitleHistory { Title = title, VideoId = videoId, Views = views, CreatedAt = clock.UtcNow };
            db.Titles.Add(t);
            if (videoId is not null) byId[videoId] = t;
            titles.Add(title);
            added++;
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new TitleImportResult(added, updated, await db.Titles.CountAsync(ct)));
    }

    public static List<(string? VideoId, string Title, int? Views)> ParseTitles(string text)
    {
        var result = new List<(string?, string, int?)>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var cols = line.Split('\t');
            if (cols.Length >= 3)
            {
                if (cols[0] == "순번") continue; // 머리글
                var title = Copywriter.Clip(cols[2], 200);
                if (title.Length == 0) continue;
                int? views = cols.Length >= 4 && double.TryParse(cols[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (int)v : null;
                var id = cols[1].Trim();
                result.Add((id.Length is > 0 and <= 20 ? id : null, title, views));
            }
            else
            {
                var title = Copywriter.Clip(cols[^1], 200);
                if (title.Length > 0) result.Add((null, title, null));
            }
        }
        return result;
    }
}
