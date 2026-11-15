using System.Security.Claims;
using System.Text.RegularExpressions;
using GoodMoNa03.Server.Auth;
using GoodMoNa03.Server.Contracts;
using GoodMoNa03.Server.Data;
using GoodMoNa03.Server.Editing;
using GoodMoNa03.Server.Infrastructure;
using GoodMoNa03.Server.Pipeline;
using GoodMoNa03.Server.Writing;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoodMoNa03.Server.Endpoints;

public static partial class DraftEndpoints
{
    private static readonly string[] VideoExtensions = [".mp4", ".mov", ".m4v", ".webm", ".mkv"];

    /// <summary>편집 권한(대표원장·원장·직원·관리자): 올리기·보기·고치기·점검 해소·완성본 받기·게시 기록.</summary>
    public static void MapEditorEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/shorts");
        g.MapPost("/sources", Upload);
        g.MapGet("/drafts", List);
        g.MapGet("/drafts/{id:int}", Get);
        g.MapPut("/drafts/{id:int}", Edit);
        g.MapPost("/drafts/{id:int}/findings/{findingId:int}/resolve", Resolve);
        g.MapPost("/drafts/{id:int}/retry", Retry);
        g.MapPost("/drafts/{id:int}/publish", Publish);
        g.MapDelete("/drafts/{id:int}", Delete);
        g.MapGet("/drafts/{id:int}/preview.mp4", (int id, AppDbContext db, DataPaths paths, CancellationToken ct) => Media(id, DraftFiles.Preview, false, db, paths, ct));
        g.MapGet("/drafts/{id:int}/final.mp4", (int id, bool? download, AppDbContext db, DataPaths paths, CancellationToken ct) => Media(id, DraftFiles.Final, download == true, db, paths, ct));
    }

    /// <summary>승인 권한(대표원장·원장): 승인·반려·승인 취소. 의료광고 책임이 있는 사람만.</summary>
    public static void MapApproverEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/shorts");
        g.MapPost("/drafts/{id:int}/approve", Approve);
        g.MapPost("/drafts/{id:int}/reject", Reject);
        g.MapPost("/drafts/{id:int}/reopen", Reopen);
    }

    /* ---------- 올리기 ---------- */

    /// <summary>
    /// 촬영본 올리기. 본문 = 영상 파일 그대로(멀티파트 아님: 큰 파일을 메모리에 올리지 않고 바로 디스크로).
    /// Content-Type은 video/* 또는 application/octet-stream만 받습니다(다른 사이트의 단순 폼 전송 차단).
    /// </summary>
    private static async Task<IResult> Upload(HttpContext http, string? name, AppDbContext db, DataPaths paths, DraftQueue queue,
        ClinicClock clock, IOptions<AppOptions> options, CancellationToken ct)
    {
        var o = options.Value;
        var type = http.Request.ContentType ?? "";
        if (!type.StartsWith("video/", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return Problem(415, "영상 파일만 올릴 수 있습니다.");
        var originalName = Path.GetFileName(name ?? "").Trim();
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        if (originalName.Length == 0 || !VideoExtensions.Contains(ext))
            return Problem(400, $"영상 파일(mp4·mov·m4v·webm·mkv)을 올려 주세요.");

        var max = (long)o.MaxUploadMegabytes * 1024 * 1024;
        if (http.Request.ContentLength is > 0 and var len && len > max)
            return Problem(413, $"파일이 너무 큽니다(최대 {o.MaxUploadMegabytes}MB).");
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = max;

        var temp = Path.Combine(paths.MediaRoot, $"upload-{Guid.NewGuid():N}.part");
        long size;
        try
        {
            await using (var file = File.Create(temp))
                await http.Request.Body.CopyToAsync(file, ct);
            size = new FileInfo(temp).Length;
        }
        catch (Exception e) when (e is BadHttpRequestException or IOException or OperationCanceledException)
        {
            TryDelete(temp);
            return e is BadHttpRequestException { StatusCode: 413 }
                ? Problem(413, $"파일이 너무 큽니다(최대 {o.MaxUploadMegabytes}MB).")
                : Problem(400, "파일을 끝까지 받지 못했습니다. 다시 올려 주세요.");
        }
        if (size == 0)
        {
            TryDelete(temp);
            return Problem(400, "빈 파일입니다.");
        }

        var (staffId, staffName) = StaffAuth.Who(http.User);
        var now = clock.UtcNow;
        SourceVideo NewSource() => new()
        {
            OriginalName = originalName.Length > 200 ? originalName[^200..] : originalName,
            StoredName = "source" + ext,
            SizeBytes = size,
            UploadedById = staffId,
            UploadedByName = staffName,
            CreatedAt = now,
        };

        // 영상 코드는 날짜별 순번이라 동시에 올리면 겹칠 수 있음 → 겹치면 다음 글자로 몇 번 다시 시도
        ShortDraft? draft = null;
        for (var attempt = 0; attempt < 5 && draft is null; attempt++)
        {
            var prefix = ShortCode.Prefix(clock.Today);
            var used = await db.Drafts.Where(d => d.Code.StartsWith(prefix)).Select(d => d.Code).ToListAsync(ct);
            var index = used.Count + attempt;
            string code;
            while (used.Contains(code = ShortCode.For(clock.Today, index))) index++;

            var candidate = new ShortDraft { Source = NewSource(), Code = code, Status = DraftStatus.Processing, Stage = "대기 중", CreatedAt = now, UpdatedAt = now };
            db.Drafts.Add(candidate);
            try
            {
                await db.SaveChangesAsync(ct);
                draft = candidate;
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }
        }
        if (draft is null)
        {
            TryDelete(temp);
            return Problem(409, "영상 코드를 정하지 못했습니다. 다시 올려 주세요.");
        }

        var folder = paths.SourceFolder(draft.SourceId);
        Directory.CreateDirectory(folder);
        File.Move(temp, Path.Combine(folder, draft.Source.StoredName), overwrite: true);
        queue.Enqueue(draft.Id, JobKind.Analyze);

        return Results.Accepted($"/api/shorts/drafts/{draft.Id}", await LoadDto(db, draft.Id, ct));
    }

    /* ---------- 보기 ---------- */

    private static async Task<IResult> List(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.Drafts.AsNoTracking()
            .OrderByDescending(d => d.Id)
            .Take(200)
            .Select(d => new DraftListItemDto(d.Id, d.Code, d.Status, d.Stage, d.Error, d.Title, d.Source.OriginalName,
                d.Source.DurationMs, d.OutputDurationMs, d.Findings.Count(f => f.Blocking && f.Resolution == null),
                d.PreviewVersion, d.Source.UploadedByName, d.CreatedAt))
            .ToListAsync(ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> Get(int id, AppDbContext db, CancellationToken ct) =>
        await LoadDto(db, id, ct) is { } dto ? Results.Ok(dto) : Problem(404, "초안을 찾을 수 없습니다.");

    private static async Task<IResult> Media(int id, string file, bool download, AppDbContext db, DataPaths paths, CancellationToken ct)
    {
        var d = await db.Drafts.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.SourceId, x.Code }).FirstOrDefaultAsync(ct);
        if (d is null) return Problem(404, "초안을 찾을 수 없습니다.");
        var path = Path.Combine(paths.SourceFolder(d.SourceId), file);
        if (!File.Exists(path)) return Problem(404, "아직 만들어지지 않은 영상입니다.");
        return Results.File(path, "video/mp4", download ? $"{d.Code}.mp4" : null, enableRangeProcessing: true);
    }

    /* ---------- 고치기 ---------- */

    private static async Task<IResult> Edit(int id, EditDraftRequest req, ClaimsPrincipal user, AppDbContext db, DraftQueue queue,
        ClinicClock clock, IOptions<AppOptions> options, CancellationToken ct)
    {
        var d = await Load(db, id, ct);
        if (d is null) return Problem(404, "초안을 찾을 수 없습니다.");
        if (d.Status is not (DraftStatus.Review or DraftStatus.Rendering))
            return Problem(409, "검토 중인 초안만 고칠 수 있습니다. 승인된 초안은 원장이 '승인 취소'한 뒤 고쳐 주세요.");
        if (req.Version != d.Version)
            return Problem(409, "다른 사람이 먼저 고쳤습니다. 화면을 새로 고친 뒤 다시 저장해 주세요.");

        var o = options.Value;
        var rerender = false;

        if (req.Title is not null)
        {
            var title = Copywriter.Clip(req.Title, 100);
            if (title.Length == 0) return Problem(400, "제목을 입력해 주세요.");
            d.Title = title;
        }
        if (req.Hook is not null)
        {
            var hook = Copywriter.Clip(req.Hook, 24);
            rerender |= hook != d.Hook;
            d.Hook = hook;
        }
        if (req.Description is not null)
        {
            if (req.Description.Length > 4000) return Problem(400, "설명이 너무 깁니다(최대 4,000자).");
            d.Description = req.Description.Replace("\r", "").Trim();
        }
        if (req.Hashtags is not null)
        {
            var tags = DraftLogic.HashtagList(req.Hashtags).Select(t => t.StartsWith('#') ? t : "#" + t).Distinct().Take(15);
            d.Hashtags = string.Join(' ', tags);
            if (d.Hashtags.Length > 300) return Problem(400, "해시태그가 너무 깁니다.");
        }
        if (req.HasPatient is { } hp) d.HasPatient = hp;
        if (req.ConsentConfirmed is { } cc) d.ConsentConfirmed = cc && d.HasPatient;

        if (req.Cues is { Count: > 0 })
        {
            var cues = DraftLogic.Cues(d);
            foreach (var e in req.Cues)
            {
                var i = cues.FindIndex(c => c.Id == e.Id);
                if (i < 0) return Problem(400, $"자막 {e.Id}번을 찾을 수 없습니다.");
                var text = e.Text.Replace("\r", "").Trim();
                if (text.Length > 80) return Problem(400, $"자막 {e.Id}번이 너무 깁니다(최대 80자).");
                text = SubtitleBuilder.Rewrap(text, o.Edit.MaxLineChars);
                if (text != cues[i].Text)
                {
                    cues[i] = cues[i] with { Text = text };
                    rerender = true;
                }
            }
            d.CuesJson = DraftLogic.Serialize(cues);
        }

        if (req.Cuts is { Count: > 0 })
        {
            var cuts = DraftLogic.Cuts(d);
            foreach (var e in req.Cuts)
            {
                if (e.Index < 0 || e.Index >= cuts.Count) return Problem(400, $"자르기 구간 {e.Index + 1}번을 찾을 수 없습니다.");
                if (cuts[e.Index].Removed == e.Removed) continue;
                cuts[e.Index] = cuts[e.Index] with { Removed = e.Removed };
                rerender = true;
            }
            d.CutsJson = DraftLogic.Serialize(cuts);
        }

        await DraftLogic.RecheckAsync(db, d, aiRisks: null, ct);

        d.Version++;
        d.UpdatedAt = clock.UtcNow;
        d.UpdatedByName = StaffAuth.Who(user).Name;
        if (rerender)
        {
            d.Status = DraftStatus.Rendering;
            d.Stage = "미리보기 다시 만드는 중";
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Problem(409, "다른 사람이 먼저 고쳤습니다. 화면을 새로 고친 뒤 다시 저장해 주세요.");
        }
        if (rerender) queue.Enqueue(d.Id, JobKind.Preview);
        return Results.Ok(await LoadDto(db, d.Id, ct));
    }

    private static async Task<IResult> Resolve(int id, int findingId, ResolveRequest req, ClaimsPrincipal user, AppDbContext db,
        ClinicClock clock, CancellationToken ct)
    {
        var d = await Load(db, id, ct);
        if (d is null) return Problem(404, "초안을 찾을 수 없습니다.");
        if (d.Status is not (DraftStatus.Review or DraftStatus.Rendering)) return Problem(409, "검토 중인 초안만 점검을 해소할 수 있습니다.");
        var f = d.Findings.FirstOrDefault(x => x.Id == findingId);
        if (f is null) return Problem(404, "점검 항목을 찾을 수 없습니다. 화면을 새로 고쳐 주세요.");
        var note = (req.Note ?? "").Trim();
        if (note.Length < 2) return Problem(400, "문제없다고 본 이유를 적어 주세요.");
        f.Resolution = note.Length > 300 ? note[..300] : note;
        f.ResolvedByName = StaffAuth.Who(user).Name;
        f.ResolvedAt = clock.UtcNow;
        d.Version++;
        d.UpdatedAt = clock.UtcNow;
        d.UpdatedByName = f.ResolvedByName;
        await db.SaveChangesAsync(ct);
        return Results.Ok(await LoadDto(db, d.Id, ct));
    }

    private static async Task<IResult> Retry(int id, AppDbContext db, DraftQueue queue, ClinicClock clock, CancellationToken ct)
    {
        var d = await Load(db, id, ct);
        if (d is null) return Problem(404, "초안을 찾을 수 없습니다.");
        if (d.Status != DraftStatus.Failed) return Problem(409, "실패한 초안만 다시 시도할 수 있습니다.");
        // 분석을 끝내지 못했으면 처음부터, 끝냈으면 미리보기만 다시
        var analyzed = d.CuesJson != "[]";
        d.Status = analyzed ? DraftStatus.Rendering : DraftStatus.Processing;
        d.Stage = "대기 중";
        d.Error = null;
        d.Version++;
        d.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        queue.Enqueue(d.Id, analyzed ? JobKind.Preview : JobKind.Analyze);
        return Results.Ok(await LoadDto(db, d.Id, ct));
    }

    private static async Task<IResult> Delete(int id, AppDbContext db, DataPaths paths, CancellationToken ct)
    {
        var d = await db.Drafts.Include(x => x.Source).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return Problem(404, "초안을 찾을 수 없습니다.");
        if (d.Status is DraftStatus.Processing or DraftStatus.Rendering or DraftStatus.Approved)
            return Problem(409, "영상을 만드는 중에는 지울 수 없습니다. 끝난 뒤 지워 주세요.");
        if (d.Status == DraftStatus.Published) return Problem(409, "게시한 쇼츠 기록은 지우지 않습니다.");
        db.Sources.Remove(d.Source);
        await db.SaveChangesAsync(ct);
        try { Directory.Delete(paths.SourceFolder(d.SourceId), recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Results.NoContent();
    }

    /* ---------- 승인·반려·게시 ---------- */

    private static async Task<IResult> Approve(int id, ApproveRequest req, ClaimsPrincipal user, AppDbContext db, DraftQueue queue,
        ClinicClock clock, CancellationToken ct)
    {
        var d = await Load(db, id, ct);
        if (d is null) return Problem(404, "초안을 찾을 수 없습니다.");
        if (req.Version != d.Version) return Problem(409, "승인하려는 사이에 초안이 바뀌었습니다. 화면을 새로 고쳐 바뀐 내용을 확인해 주세요.");
        var blockers = DraftLogic.ApprovalBlockers(d);
        if (blockers.Count > 0) return Problem(409, string.Join(" ", blockers));

        var (staffId, name) = StaffAuth.Who(user);
        d.Status = DraftStatus.Approved;
        d.Stage = "완성본 만드는 중";
        d.ApprovedById = staffId;
        d.ApprovedByName = name;
        d.ApprovedAt = clock.UtcNow;
        d.RejectReason = null;
        d.RejectedByName = null;
        d.Version++;
        d.UpdatedAt = clock.UtcNow;
        d.UpdatedByName = name;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Problem(409, "승인하려는 사이에 초안이 바뀌었습니다. 화면을 새로 고쳐 주세요.");
        }
        queue.Enqueue(d.Id, JobKind.Final);
        return Results.Ok(await LoadDto(db, d.Id, ct));
    }

    private static async Task<IResult> Reject(int id, RejectRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var d = await Load(db, id, ct);
        if (d is null) return Problem(404, "초안을 찾을 수 없습니다.");
        if (d.Status is not (DraftStatus.Review or DraftStatus.Rendering)) return Problem(409, "검토 중인 초안만 반려할 수 있습니다.");
        var reason = (req.Reason ?? "").Trim();
        if (reason.Length < 2) return Problem(400, "반려 이유를 적어 주세요.");
        var name = StaffAuth.Who(user).Name;
        d.Status = DraftStatus.Rejected;
        d.Stage = null;
        d.RejectReason = reason.Length > 300 ? reason[..300] : reason;
        d.RejectedByName = name;
        d.Version++;
        d.UpdatedAt = clock.UtcNow;
        d.UpdatedByName = name;
        await db.SaveChangesAsync(ct);
        return Results.Ok(await LoadDto(db, d.Id, ct));
    }

    /// <summary>승인·반려를 되돌려 다시 검토로. 완성본은 지웁니다(고친 뒤 다시 승인해야 새로 만듦).</summary>
    private static async Task<IResult> Reopen(int id, ClaimsPrincipal user, AppDbContext db, DataPaths paths, ClinicClock clock, CancellationToken ct)
    {
        var d = await Load(db, id, ct);
        if (d is null) return Problem(404, "초안을 찾을 수 없습니다.");
        if (d.Status is not (DraftStatus.Ready or DraftStatus.Rejected)) return Problem(409, "완성본 준비 또는 반려 상태에서만 다시 검토로 돌릴 수 있습니다.");
        var name = StaffAuth.Who(user).Name;
        d.Status = DraftStatus.Review;
        d.ApprovedById = null;
        d.ApprovedByName = null;
        d.ApprovedAt = null;
        d.Version++;
        d.UpdatedAt = clock.UtcNow;
        d.UpdatedByName = name;
        await db.SaveChangesAsync(ct);
        TryDelete(Path.Combine(paths.SourceFolder(d.SourceId), DraftFiles.Final));
        return Results.Ok(await LoadDto(db, d.Id, ct));
    }

    /// <summary>
    /// 유튜브에 올린 뒤 기록: 주소와 '관련 동영상' 연결 여부(분석 보고서 S3). 제목은 반복 제목 경고 목록에 들어갑니다.
    /// </summary>
    private static async Task<IResult> Publish(int id, PublishRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, CancellationToken ct)
    {
        var d = await Load(db, id, ct);
        if (d is null) return Problem(404, "초안을 찾을 수 없습니다.");
        if (d.Status != DraftStatus.Ready) return Problem(409, "완성본이 준비된 초안만 게시 기록을 남길 수 있습니다.");
        var videoId = YouTubeVideoId(req.YouTubeUrl ?? "");
        if (videoId is null) return Problem(400, "유튜브 주소를 확인해 주세요. 예: https://youtube.com/shorts/abc123");

        var name = StaffAuth.Who(user).Name;
        d.Status = DraftStatus.Published;
        d.YouTubeUrl = $"https://youtube.com/shorts/{videoId}";
        d.RelatedVideoLinked = req.RelatedVideoLinked;
        d.PublishedAt = clock.UtcNow;
        d.PublishedByName = name;
        d.Version++;
        d.UpdatedAt = clock.UtcNow;
        d.UpdatedByName = name;
        if (!await db.Titles.AnyAsync(t => t.VideoId == videoId, ct))
            db.Titles.Add(new TitleHistory { Title = d.Title, VideoId = videoId, CreatedAt = clock.UtcNow });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await LoadDto(db, d.Id, ct));
    }

    /// <summary>youtube.com/shorts/ID · youtu.be/ID · youtube.com/watch?v=ID → ID</summary>
    public static string? YouTubeVideoId(string url)
    {
        var m = YouTubePattern().Match(url.Trim());
        return m.Success ? m.Groups["id"].Value : null;
    }

    [GeneratedRegex(@"^https?://(?:(?:www\.|m\.)?youtube\.com/(?:shorts/|watch\?(?:.*&)?v=)|youtu\.be/)(?<id>[A-Za-z0-9_-]{6,20})(?:[?&#/].*)?$")]
    private static partial Regex YouTubePattern();

    /* ---------- 공통 ---------- */

    private static Task<ShortDraft?> Load(AppDbContext db, int id, CancellationToken ct) =>
        db.Drafts.Include(d => d.Source).Include(d => d.Findings).FirstOrDefaultAsync(d => d.Id == id, ct);

    public static async Task<DraftDto?> LoadDto(AppDbContext db, int id, CancellationToken ct)
    {
        var d = await db.Drafts.AsNoTracking().Include(x => x.Source).Include(x => x.Findings).FirstOrDefaultAsync(x => x.Id == id, ct);
        return d is null ? null : ToDto(d);
    }

    public static DraftDto ToDto(ShortDraft d)
    {
        var s = d.Source;
        return new DraftDto(
            d.Id, d.Code, d.Status, d.Stage, d.Error, d.Version, d.PreviewVersion,
            new SourceDto(s.OriginalName, s.SizeBytes, s.DurationMs, s.Width, s.Height, s.UploadedByName, s.CreatedAt, s.SttModel, s.SttRuntime, s.SttElapsedMs),
            d.OutputDurationMs,
            d.Hook, DraftLogic.Strings(d.HookCandidatesJson), d.Title, DraftLogic.Strings(d.TitleCandidatesJson),
            d.Summary, d.Description, d.Hashtags, DraftLogic.Strings(d.EmphasisJson), d.AiNote,
            d.HasPatient, d.ConsentConfirmed,
            DraftLogic.Cues(d).Select(c => new CueDto(c.Id, c.StartMs, c.EndMs, c.Text)).ToList(),
            DraftLogic.Cuts(d).Select((c, i) => new CutDto(i, c.StartMs, c.EndMs, c.Removed)).ToList(),
            d.Findings.OrderBy(f => f.Source == FindingSource.Similar).ThenBy(f => f.Id)
                .Select(f => new FindingDto(f.Id, f.Source, f.Where, f.Rule, f.Excerpt, f.Reason, f.Blocking, f.Resolution, f.ResolvedByName, f.ResolvedAt)).ToList(),
            d.Status == DraftStatus.Review ? DraftLogic.ApprovalBlockers(d) : [],
            d.UpdatedByName, d.UpdatedAt,
            d.ApprovedByName, d.ApprovedAt,
            d.RejectReason, d.RejectedByName,
            d.YouTubeUrl, d.RelatedVideoLinked, d.PublishedAt, d.PublishedByName);
    }

    public static IResult Problem(int status, string detail) => Results.Problem(detail: detail, statusCode: status);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
