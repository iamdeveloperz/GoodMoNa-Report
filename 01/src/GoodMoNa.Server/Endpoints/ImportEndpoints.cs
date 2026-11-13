using System.Text;
using GoodMoNa.Server.Contracts;
using GoodMoNa.Server.Import;
using GoodMoNa.Server.Infrastructure;
using Microsoft.AspNetCore.Http.Features;

namespace GoodMoNa.Server.Endpoints;

/// <summary>
/// 이전 전산 기록 가져오기 (/import 화면).
/// 사진: 웹에서 한 장씩 올리거나(PUT /api/import/files/…), 많으면 서버 PC의 가져오기 폴더에 직접 복사합니다.
/// CSV: 미리보기(preview)로 검사한 뒤 실행(run)하면 백그라운드에서 트랜잭션 하나로 저장합니다.
/// </summary>
public static class ImportEndpoints
{
    private const long MaxCsvBytes = 20 * 1024 * 1024;
    private const int MaxListedFiles = 50_000;

    public static void MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/import");
        g.MapGet("/template.csv", () =>
            Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(LegacyImporter.TemplateCsv)).ToArray(),
                "text/csv; charset=utf-8", "goodmona-import-template.csv"));
        g.MapGet("/files", ListFiles);
        g.MapPut("/files/{**path}", PutFile);
        g.MapDelete("/files", ClearFiles);
        g.MapPost("/preview", Preview).DisableAntiforgery();
        g.MapPost("/run", Run).DisableAntiforgery();
        g.MapGet("/status", (ImportJobs jobs) => Results.Ok(jobs.Status));
    }

    private static IResult ListFiles(DataPaths paths)
    {
        var root = paths.ImportRoot;
        Directory.CreateDirectory(root);
        var files = new List<ImportFileDto>();
        int count = 0;
        long total = 0;
        foreach (var info in new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (info.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
            count++;
            total += info.Length;
            if (files.Count < MaxListedFiles) files.Add(new ImportFileDto(PhotoIndex.Normalize(Path.GetRelativePath(root, info.FullName)), info.Length));
        }
        return Results.Ok(new ImportFolderDto(root, count, total, files));
    }

    /// <summary>사진 한 장을 가져오기 폴더에 저장합니다. 본문이 곧 파일입니다 (multipart 아님). 같은 경로면 덮어씁니다.</summary>
    private static async Task<IResult> PutFile(string? path, HttpContext http, DataPaths paths, ImportJobs jobs, CancellationToken ct)
    {
        if (jobs.IsRunning) return Problems.Conflict("가져오기가 진행 중입니다. 끝난 뒤 올려 주세요.");
        if (!TrySafeRelative(path, out var relative))
            return Problems.Validation("path", "파일 경로가 올바르지 않습니다. (JPG·PNG·WebP만, 상위 폴더 이동 불가)");

        var limit = LegacyImporter.MaxPhotoBytes;
        if (http.Request.ContentLength > limit) return Problems.TooLarge($"사진은 한 장에 {limit / 1024 / 1024}MB까지 올릴 수 있습니다.");
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = limit + 1;

        var full = Path.GetFullPath(Path.Combine(paths.ImportRoot, relative));
        if (!full.StartsWith(paths.ImportRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return Problems.Validation("path", "파일 경로가 올바르지 않습니다.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var temp = $"{full}.{Guid.NewGuid():N}.part";
        try
        {
            long written = 0;
            var head = new byte[12];
            var headLen = 0;
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int n;
                while ((n = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
                {
                    written += n;
                    if (written > limit) return Problems.TooLarge($"사진은 한 장에 {limit / 1024 / 1024}MB까지 올릴 수 있습니다.");
                    if (headLen < head.Length)
                    {
                        var take = Math.Min(head.Length - headLen, n);
                        Array.Copy(buffer, 0, head, headLen, take);
                        headLen += take;
                    }
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                }
            }
            if (written == 0) return Problems.Validation("file", "빈 파일입니다.");
            if (MediaStorage.SniffImage(head.AsSpan(0, headLen)) is null)
                return Problems.Unsupported("JPG·PNG·WebP 사진만 올릴 수 있습니다. (아이폰 HEIC는 JPG로 바꿔 주세요)");

            File.Move(temp, full, overwrite: true);
            return Results.Ok(new ImportFileDto(relative, written));
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static IResult ClearFiles(DataPaths paths, ImportJobs jobs)
    {
        if (jobs.IsRunning) return Problems.Conflict("가져오기가 진행 중입니다.");
        var root = paths.ImportRoot;
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.EnumerateDirectories(root)) Directory.Delete(dir, recursive: true);
            foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> Preview(IFormFile? file, LegacyImporter importer, CancellationToken ct)
    {
        var csv = await ReadCsv(file, ct);
        if (csv.Error is not null) return csv.Error;
        var plan = await importer.PlanAsync(csv.Bytes!, ct);
        return Results.Ok(LegacyImporter.ToPreview(plan));
    }

    /// <summary>
    /// 검사를 다시 한 뒤 백그라운드로 저장을 시작합니다. 오류 행이 있으면 기본은 거절(422)이고,
    /// skipInvalid=true면 오류 행만 빼고 가져옵니다.
    /// </summary>
    private static async Task<IResult> Run(IFormFile? file, bool? skipInvalid, LegacyImporter importer, ImportJobs jobs, CancellationToken ct)
    {
        if (jobs.IsRunning) return Problems.Conflict("이미 가져오기가 진행 중입니다.");
        var csv = await ReadCsv(file, ct);
        if (csv.Error is not null) return csv.Error;

        var plan = await importer.PlanAsync(csv.Bytes!, ct);
        var preview = LegacyImporter.ToPreview(plan);
        var headerBroken = plan.Errors.Any(e => e.Row == 1);
        if (headerBroken || (plan.HasErrors && skipInvalid != true))
            return Results.Problem(
                detail: headerBroken ? plan.Errors[0].Message : $"오류가 있는 행이 {plan.ErrorRows}개 있습니다. 고쳐서 다시 올리거나, 오류 행을 빼고 가져오세요.",
                statusCode: StatusCodes.Status422UnprocessableEntity, title: "가져올 수 없음",
                extensions: new Dictionary<string, object?> { ["preview"] = preview });
        if (plan.Patients.Count == 0) return Problems.Validation("file", "가져올 행이 없습니다.");

        if (!jobs.TryStart(plan)) return Problems.Conflict("이미 가져오기가 진행 중입니다.");
        return Results.Accepted("/api/import/status", jobs.Status);
    }

    private static async Task<(byte[]? Bytes, IResult? Error)> ReadCsv(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return (null, Problems.Validation("file", "CSV 파일을 골라 주세요."));
        if (file.Length > MaxCsvBytes) return (null, Problems.TooLarge($"CSV는 {MaxCsvBytes / 1024 / 1024}MB까지 올릴 수 있습니다. 나눠서 올려 주세요."));
        using var ms = new MemoryStream((int)file.Length);
        await file.CopyToAsync(ms, ct);
        return (ms.ToArray(), null);
    }

    private static bool TrySafeRelative(string? path, out string relative)
    {
        relative = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is 0 or > 8) return false;
        var bad = Path.GetInvalidFileNameChars();
        foreach (var p in parts)
            if (p is "." or ".." || p.Length > 150 || p.IndexOfAny(bad) >= 0 || p.EndsWith('.')) return false;
        if (!LegacyImporter.PhotoExtensions.Contains(Path.GetExtension(parts[^1]).ToLowerInvariant())) return false;
        relative = string.Join('/', parts);
        return true;
    }
}
