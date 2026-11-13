using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GoodMoNa.Server.Contracts;
using GoodMoNa.Server.Data;
using GoodMoNa.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa.Server.Import;

/// <summary>
/// 이전 전산의 환자 기록(CSV + 사진 파일)을 한 번에 DB로 옮깁니다.
/// 1) <see cref="PlanAsync"/>: CSV를 읽어 행마다 검사하고, 무엇이 새로 생기는지 계산합니다 (저장 안 함).
/// 2) <see cref="ApplyAsync"/>: 계획을 트랜잭션 하나로 저장합니다. 중간에 실패하면 DB와 복사한 사진을 모두 되돌립니다.
/// 같은 CSV를 다시 돌려도 환자(이름+생년월일)·방문(환자+날짜)·사진(SHA-256)이 겹치지 않습니다.
/// </summary>
public sealed partial class LegacyImporter(AppDbContext db, DataPaths paths, MediaStorage storage, ClinicClock clock, ILogger<LegacyImporter> log)
{
    public const string DefaultReporter = "이전 기록";
    public const int MaxRows = 200_000;
    public const int MaxMemoLength = 500;
    public const int MaxReporterLength = 30;
    public const long MaxPhotoBytes = 50L * 1024 * 1024;
    public static readonly string[] PhotoExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>머리글 이름 → 열 종류. 공백·밑줄·대소문자는 무시합니다.</summary>
    private static readonly Dictionary<string, string> Columns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["이름"] = "name", ["환자명"] = "name", ["성명"] = "name", ["name"] = "name",
        ["생년월일"] = "birth", ["생일"] = "birth", ["birth"] = "birth", ["birthdate"] = "birth",
        ["방문일"] = "date", ["내원일"] = "date", ["진료일"] = "date", ["date"] = "date", ["visitdate"] = "date",
        ["경과"] = "progress", ["progress"] = "progress",
        ["메모"] = "memo", ["비고"] = "memo", ["memo"] = "memo",
        ["사진"] = "photos", ["사진파일"] = "photos", ["photo"] = "photos", ["photos"] = "photos",
        ["보고자"] = "by", ["담당자"] = "by", ["reportedby"] = "by",
    };

    private static readonly Dictionary<string, Progress> ProgressWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["호전"] = Progress.Improved, ["개선"] = Progress.Improved, ["좋아짐"] = Progress.Improved, ["improved"] = Progress.Improved,
        ["유지"] = Progress.Unchanged, ["변화없음"] = Progress.Unchanged, ["동일"] = Progress.Unchanged, ["unchanged"] = Progress.Unchanged,
        ["악화"] = Progress.Worsened, ["나빠짐"] = Progress.Worsened, ["worsened"] = Progress.Worsened,
    };

    public static string TemplateCsv =>
        "이름,생년월일,방문일,경과,메모,사진,보고자\r\n" +
        "김민수,1988-04-17,2025-03-02,유지,\"정수리 밀도 낮음, 미녹시딜 시작\",김민수_20250302.jpg,\r\n" +
        "김민수,1988-04-17,2025-04-06,호전,,김민수_20250406.jpg;김민수_20250406_2.jpg,김간호\r\n" +
        "이서연,920708,,,,,\r\n";

    /* ---------- 1. 계획 ---------- */

    public async Task<ImportPlan> PlanAsync(byte[] csv, CancellationToken ct)
    {
        var text = CsvTable.Decode(csv, out var encoding);
        var rows = CsvTable.Parse(text);
        var plan = new ImportPlan { Encoding = encoding };

        if (rows.Count == 0)
        {
            plan.AddError(1, "빈 파일입니다.");
            return plan;
        }

        var col = new Dictionary<string, int>();
        for (var i = 0; i < rows[0].Length; i++)
        {
            var key = HeaderNoise().Replace(rows[0][i], "");
            if (Columns.TryGetValue(key, out var kind)) col.TryAdd(kind, i);
        }
        if (!col.ContainsKey("name") || !col.ContainsKey("birth"))
        {
            plan.AddError(1, "머리글(1행)에 '이름'과 '생년월일' 열이 있어야 합니다. 양식을 내려받아 맞춰 주세요.");
            return plan;
        }
        if (rows.Count - 1 > MaxRows)
        {
            plan.AddError(1, $"한 번에 {MaxRows:N0}행까지 가져올 수 있습니다. 파일을 나눠 주세요.");
            return plan;
        }

        string Cell(string[] r, string kind) => col.TryGetValue(kind, out var i) && i < r.Length ? r[i].Trim() : "";

        var today = clock.Today;
        var photos = PhotoIndex.Build(paths.ImportRoot);
        var patients = new Dictionary<(string, DateOnly), PatientPlan>();

        for (var n = 1; n < rows.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            var r = rows[n];
            var rowNo = n + 1;
            if (r.All(string.IsNullOrWhiteSpace)) continue;
            plan.Rows++;

            var errors = new List<string>();

            var name = PatientInput.NormalizeName(Cell(r, "name"));
            if (name.Length is 0 or > PatientInput.MaxNameLength) errors.Add($"이름은 1~{PatientInput.MaxNameLength}자");

            var birthText = Cell(r, "birth");
            DateOnly birth = default;
            if (!PatientInput.TryParseBirth(birthText, today, out birth) &&
                !(TryParseDate(birthText, out birth) && birth.Year >= 1900 && birth <= today))
                errors.Add($"생년월일 '{birthText}'을(를) 읽을 수 없음");

            var dateText = Cell(r, "date");
            DateOnly? date = null;
            if (dateText.Length > 0)
            {
                if (!TryParseDate(dateText, out var d)) errors.Add($"방문일 '{dateText}'을(를) 읽을 수 없음");
                else if (d > today) errors.Add("방문일이 오늘보다 뒤");
                else if (birth != default && d < birth) errors.Add("방문일이 생년월일보다 앞");
                else date = d;
            }

            var progressText = Cell(r, "progress");
            Progress? progress = null;
            if (progressText.Length > 0)
            {
                if (ProgressWords.TryGetValue(progressText.Replace(" ", ""), out var p)) progress = p;
                else errors.Add($"경과 '{progressText}' (호전·유지·악화 중 하나)");
            }

            var memo = Cell(r, "memo");
            if (memo.Length > MaxMemoLength) errors.Add($"메모가 {MaxMemoLength}자를 넘음 ({memo.Length}자)");

            var by = Cell(r, "by");
            if (by.Length > MaxReporterLength) errors.Add($"보고자는 {MaxReporterLength}자까지");

            var files = new List<PhotoFile>();
            foreach (var reference in Cell(r, "photos").Split([';', '|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var found = photos.Resolve(reference, out var problem);
                if (found is null) errors.Add(problem!);
                else files.Add(found);
            }

            if (dateText.Length == 0 && (progress is not null || memo.Length > 0 || files.Count > 0))
                errors.Add("경과·메모·사진이 있으면 방문일이 있어야 함");

            // 같은 환자·같은 날짜가 여러 행이면 합칩니다. 경과가 서로 다르면 어느 쪽이 맞는지 알 수 없으므로 오류.
            PatientPlan? pp = null;
            VisitPlan? vp = null;
            if (errors.Count == 0)
            {
                pp = patients.GetValueOrDefault((name, birth));
                if (date is { } day && pp?.Visits.GetValueOrDefault(day) is { } existing)
                {
                    vp = existing;
                    if (progress is not null && vp.Progress is not null && vp.Progress != progress)
                        errors.Add($"{vp.FirstRow}행과 같은 방문인데 경과가 다름");
                }
            }

            if (errors.Count > 0)
            {
                plan.ErrorRows++;
                plan.AddError(rowNo, string.Join(" · ", errors));
                continue;
            }

            if (pp is null) patients[(name, birth)] = pp = new PatientPlan { Name = name, Birth = birth };
            if (date is not { } visitDate) continue;

            if (vp is null) pp.Visits[visitDate] = vp = new VisitPlan { Date = visitDate, FirstRow = rowNo };
            vp.Progress ??= progress;
            if (memo.Length > 0 && !vp.Memos.Contains(memo)) vp.Memos.Add(memo);
            if (by.Length > 0) vp.ReportedBy ??= by;
            foreach (var f in files)
                if (!vp.Photos.Any(x => string.Equals(x.FullPath, f.FullPath, StringComparison.OrdinalIgnoreCase))) vp.Photos.Add(f);
            if (string.Join("\n", vp.Memos).Length > MaxMemoLength)
                plan.AddWarning(rowNo, $"같은 방문의 메모를 합치면 {MaxMemoLength}자를 넘어 뒷부분이 잘립니다");
        }

        plan.Patients.AddRange(patients.Values);
        await MarkExistingAsync(plan, ct);
        return plan;
    }

    /// <summary>이미 DB에 있는 환자·방문을 표시합니다 (미리보기 숫자와 저장 방식이 달라짐).</summary>
    private async Task MarkExistingAsync(ImportPlan plan, CancellationToken ct)
    {
        var names = plan.Patients.Select(p => p.Name).Distinct().ToList();
        var existing = await db.Patients.AsNoTracking()
            .Where(p => names.Contains(p.Name))
            .Select(p => new { p.Id, p.Name, p.BirthDate })
            .ToListAsync(ct);
        var ids = existing.ToDictionary(p => (p.Name, p.BirthDate), p => p.Id);

        foreach (var pp in plan.Patients)
            if (ids.TryGetValue((pp.Name, pp.Birth), out var id)) pp.ExistingId = id;

        var patientIds = plan.Patients.Where(p => p.ExistingId is not null).Select(p => p.ExistingId!.Value).ToList();
        var visits = await db.Visits.AsNoTracking()
            .Where(v => patientIds.Contains(v.PatientId))
            .Select(v => new { v.PatientId, v.VisitDate })
            .ToListAsync(ct);
        var have = visits.Select(v => (v.PatientId, v.VisitDate)).ToHashSet();
        foreach (var pp in plan.Patients.Where(p => p.ExistingId is not null))
            foreach (var vp in pp.Visits.Values)
                vp.Exists = have.Contains((pp.ExistingId!.Value, vp.Date));
    }

    /* ---------- 2. 저장 ---------- */

    public async Task<ImportResultDto> ApplyAsync(ImportPlan plan, Action<int, int>? progress, CancellationToken ct)
    {
        var copied = new List<string>();
        int newPatients = 0, newVisits = 0, updatedVisits = 0, photosAdded = 0, photosSkipped = 0, done = 0, photosDone = 0;
        var now = clock.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var pp in plan.Patients)
            {
                ct.ThrowIfCancellationRequested();
                Patient patient;
                if (pp.ExistingId is { } id) patient = await db.Patients.FirstAsync(p => p.Id == id, ct);
                else
                {
                    patient = new Patient { Name = pp.Name, BirthDate = pp.Birth, CreatedAt = now };
                    db.Patients.Add(patient);
                    newPatients++;
                }

                foreach (var vp in pp.Visits.Values)
                {
                    // 그날 정오(병원 시각)에 보고된 것으로 둡니다. 실제 시각은 이전 전산에 없는 경우가 많습니다.
                    var at = clock.ToUtc(vp.Date, new TimeOnly(12, 0));
                    var memo = Truncate(string.Join("\n", vp.Memos), MaxMemoLength);
                    var visit = vp.Exists
                        ? await db.Visits.FirstAsync(v => v.PatientId == patient.Id && v.VisitDate == vp.Date, ct)
                        : null;
                    HashSet<string> hashes = [];

                    if (visit is null)
                    {
                        visit = new Visit
                        {
                            Patient = patient,
                            VisitDate = vp.Date,
                            Status = VisitStatus.Reported,
                            Progress = vp.Progress,
                            Memo = memo,
                            CreatedAt = at,
                            ReportedAt = at,
                            ReportedBy = vp.ReportedBy ?? DefaultReporter,
                        };
                        db.Visits.Add(visit);
                        newVisits++;
                    }
                    else
                    {
                        // 이미 있는 방문은 덮어쓰지 않고, 비어 있는 칸만 채우고 사진을 보탭니다.
                        var changed = false;
                        if (visit.Progress is null && vp.Progress is not null) { visit.Progress = vp.Progress; changed = true; }
                        if (memo.Length > 0 && !visit.Memo.Contains(memo, StringComparison.Ordinal))
                        {
                            visit.Memo = Truncate(visit.Memo.Length == 0 ? memo : $"{visit.Memo}\n{memo}", MaxMemoLength);
                            changed = true;
                        }
                        if (changed) updatedVisits++;
                        hashes = (await db.Photos.Where(p => p.VisitId == visit.Id && p.ContentHash != null)
                            .Select(p => p.ContentHash!).ToListAsync(ct)).ToHashSet();
                    }

                    foreach (var photo in vp.Photos)
                    {
                        ct.ThrowIfCancellationRequested();
                        var hash = await HashAsync(photo.FullPath, ct);
                        if (!hashes.Add(hash))
                        {
                            photosSkipped++;
                        }
                        else
                        {
                            var relative = storage.CopyIn(photo.FullPath, "photos", photo.Extension, at);
                            copied.Add(relative);
                            visit.Photos.Add(new Photo
                            {
                                StoragePath = relative,
                                ContentType = photo.ContentType,
                                SizeBytes = new FileInfo(photo.FullPath).Length,
                                UploadedAt = at,
                                ContentHash = hash,
                            });
                            photosAdded++;
                        }
                        progress?.Invoke(done, ++photosDone);
                    }
                }

                // 변경 추적이 너무 커지지 않도록 환자 200명마다 내보냅니다 (아직 커밋 전이라 실패하면 함께 되돌아감).
                if (++done % 200 == 0)
                {
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                }
                progress?.Invoke(done, photosDone);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            foreach (var relative in copied) storage.TryDelete(relative);
            log.LogWarning("이전 기록 가져오기 실패: 사진 {Count}장 복사본을 지우고 되돌렸습니다.", copied.Count);
            throw;
        }

        return new ImportResultDto(newPatients, newVisits, updatedVisits, photosAdded, photosSkipped, plan.ErrorRows);
    }

    public static ImportPreviewDto ToPreview(ImportPlan plan) => new(
        plan.Encoding,
        plan.Rows,
        plan.Patients.Count,
        plan.Patients.Count(p => p.ExistingId is null),
        plan.Patients.Sum(p => p.Visits.Count),
        plan.Patients.Sum(p => p.Visits.Values.Count(v => !v.Exists)),
        plan.PhotoCount,
        plan.ErrorRows,
        plan.Errors,
        plan.Warnings);

    /* ---------- 도우미 ---------- */

    /// <summary>2025-03-02, 2025.3.2, 2025/03/02 0:00, 20250302 를 받습니다. (엑셀이 시각을 붙여 내보내는 경우 포함)</summary>
    public static bool TryParseDate(string? text, out DateOnly date)
    {
        date = default;
        var nums = Numbers().Matches(text ?? "").Select(m => m.Value).ToList();
        if (nums.Count == 0) return false;
        int y, m, d;
        if (nums[0].Length == 8)
        {
            y = int.Parse(nums[0][..4], CultureInfo.InvariantCulture);
            m = int.Parse(nums[0][4..6], CultureInfo.InvariantCulture);
            d = int.Parse(nums[0][6..], CultureInfo.InvariantCulture);
        }
        else if (nums.Count >= 3 && nums[0].Length == 4 && nums[1].Length <= 2 && nums[2].Length <= 2)
        {
            y = int.Parse(nums[0], CultureInfo.InvariantCulture);
            m = int.Parse(nums[1], CultureInfo.InvariantCulture);
            d = int.Parse(nums[2], CultureInfo.InvariantCulture);
        }
        else return false;

        if (y < 1900 || m is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        date = new DateOnly(y, m, d);
        return true;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"[\s_\-]")]
    private static partial Regex HeaderNoise();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();
}

public sealed class ImportPlan
{
    public required string Encoding { get; init; }
    public int Rows { get; set; }
    public int ErrorRows { get; set; }
    public List<PatientPlan> Patients { get; } = [];
    public List<ImportIssue> Errors { get; } = [];
    public List<ImportIssue> Warnings { get; } = [];
    public int PhotoCount => Patients.Sum(p => p.Visits.Values.Sum(v => v.Photos.Count));
    public bool HasErrors => ErrorRows > 0 || Errors.Count > 0;

    // 화면에 수천 줄을 보내지 않도록 앞부분만 담고, 개수는 ErrorRows로 알려 줍니다.
    public void AddError(int row, string message)
    {
        if (Errors.Count < 300) Errors.Add(new ImportIssue(row, message));
    }

    public void AddWarning(int row, string message)
    {
        if (Warnings.Count < 300) Warnings.Add(new ImportIssue(row, message));
    }
}

public sealed class PatientPlan
{
    public required string Name { get; init; }
    public DateOnly Birth { get; init; }
    public int? ExistingId { get; set; }
    public SortedDictionary<DateOnly, VisitPlan> Visits { get; } = [];
}

public sealed class VisitPlan
{
    public DateOnly Date { get; init; }
    public int FirstRow { get; init; }
    public Progress? Progress { get; set; }
    public List<string> Memos { get; } = [];
    public string? ReportedBy { get; set; }
    public List<PhotoFile> Photos { get; } = [];
    public bool Exists { get; set; }
}

public sealed record PhotoFile(string FullPath, string Extension, string ContentType);
