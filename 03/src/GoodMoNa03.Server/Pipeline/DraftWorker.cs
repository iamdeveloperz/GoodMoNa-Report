using System.Threading.Channels;
using GoodMoNa03.Server.Data;
using GoodMoNa03.Server.Editing;
using GoodMoNa03.Server.Infrastructure;
using GoodMoNa03.Server.Media;
using GoodMoNa03.Server.Writing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoodMoNa03.Server.Pipeline;

public enum JobKind
{
    /// <summary>받아쓰기·무음·자막·초안·점검 → 미리보기</summary>
    Analyze,

    /// <summary>검토에서 고친 내용으로 미리보기 다시</summary>
    Preview,

    /// <summary>승인 후 게시용 1080×1920</summary>
    Final,
}

public sealed record DraftJob(int DraftId, JobKind Kind);

/// <summary>작업 대기열. GPU를 01(Whisper)·02(Ollama)와 나눠 쓰므로 한 번에 하나씩 처리합니다.</summary>
public sealed class DraftQueue
{
    private readonly Channel<DraftJob> _channel = Channel.CreateUnbounded<DraftJob>();

    public ChannelReader<DraftJob> Reader => _channel.Reader;

    public void Enqueue(int draftId, JobKind kind) => _channel.Writer.TryWrite(new DraftJob(draftId, kind));
}

/// <summary>파일 이름 규칙(초안 폴더 안).</summary>
public static class DraftFiles
{
    public const string Audio = "audio.wav";
    public const string Ass = "sub.ass";
    public const string Preview = "preview.mp4";
    public const string Final = "final.mp4";
}

public sealed class DraftWorker(
    IServiceScopeFactory scopes,
    DraftQueue queue,
    IVideoTool video,
    ISpeechToText stt,
    DataPaths paths,
    IOptions<AppOptions> options,
    ILogger<DraftWorker> log) : BackgroundService
{
    private readonly AppOptions _o = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        await Task.Yield();
        await RecoverAsync(stopping);

        await foreach (var job in queue.Reader.ReadAllAsync(stopping))
        {
            try
            {
                await RunAsync(job, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break; // 서버 종료: 다음 시작 때 RecoverAsync가 다시 넣음
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "초안 {DraftId} {Kind} 실패", job.DraftId, job.Kind);
                await MarkFailedAsync(job.DraftId, ex is FfmpegException or FileNotFoundException or InvalidDataException
                    ? ex.Message
                    : $"처리 중 오류: {ex.Message}");
            }
        }
    }

    /// <summary>서버가 꺼질 때 진행 중이던 작업을 다시 대기열에 넣습니다.</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = await db.Drafts
            .Where(d => d.Status == DraftStatus.Processing || d.Status == DraftStatus.Rendering || d.Status == DraftStatus.Approved)
            .OrderBy(d => d.Id).Select(d => new { d.Id, d.Status }).ToListAsync(ct);
        foreach (var p in pending)
            queue.Enqueue(p.Id, p.Status switch
            {
                DraftStatus.Processing => JobKind.Analyze,
                DraftStatus.Rendering => JobKind.Preview,
                _ => JobKind.Final,
            });
        if (pending.Count > 0) log.LogInformation("이어서 처리할 초안 {Count}개를 대기열에 넣었습니다.", pending.Count);
    }

    private async Task RunAsync(DraftJob job, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var draft = await db.Drafts.Include(d => d.Source).Include(d => d.Findings).FirstOrDefaultAsync(d => d.Id == job.DraftId, ct);
        if (draft is null) return;

        switch (job.Kind)
        {
            case JobKind.Analyze when draft.Status == DraftStatus.Processing:
                await AnalyzeAsync(db, draft, scope.ServiceProvider.GetRequiredService<Copywriter>(), ct);
                break;
            case JobKind.Preview when draft.Status == DraftStatus.Rendering:
                await RenderAsync(db, draft, preview: true, ct);
                break;
            case JobKind.Final when draft.Status == DraftStatus.Approved:
                await RenderAsync(db, draft, preview: false, ct);
                break;
            // 그 밖: 그사이 상태가 바뀜(반려·삭제·이미 처리됨) → 건너뜀
        }
    }

    private async Task AnalyzeAsync(AppDbContext db, ShortDraft d, Copywriter copywriter, CancellationToken ct)
    {
        var s = d.Source;
        var folder = paths.SourceFolder(s.Id);
        var sourcePath = Path.Combine(folder, s.StoredName);

        await StageAsync(db, d, "영상 정보 읽는 중", ct);
        var probe = await video.ProbeAsync(sourcePath, ct);
        if (!probe.HasAudio) throw new InvalidDataException("소리가 없는 영상입니다. 원장 음성이 녹음된 촬영본을 올려 주세요.");
        if (probe.DurationMs <= 0 || probe.Width <= 0) throw new InvalidDataException("영상 길이나 크기를 읽지 못했습니다.");
        if (probe.DurationMs > _o.MaxSourceMinutes * 60_000)
            throw new InvalidDataException($"촬영본이 {_o.MaxSourceMinutes}분보다 깁니다. 쇼츠 한 편 분량만 올려 주세요(롱폼에서 잘라내기는 2단계).");
        s.DurationMs = probe.DurationMs;
        s.Width = probe.Width;
        s.Height = probe.Height;

        await StageAsync(db, d, "무음 구간 찾는 중", ct);
        var silences = await video.DetectSilenceAsync(sourcePath, _o.Edit.SilenceNoiseDb, _o.Edit.MinSilenceMs, ct);

        await StageAsync(db, d, "음성 인식 중", ct);
        var wav = Path.Combine(folder, DraftFiles.Audio);
        await video.ExtractAudioAsync(sourcePath, wav, ct);
        WordTranscript transcript;
        try
        {
            transcript = await stt.TranscribeAsync(wav, ct);
        }
        finally
        {
            TryDelete(wav);
        }
        if (transcript.Words.Count == 0) throw new InvalidDataException("말소리를 알아듣지 못했습니다. 음성이 들리는 촬영본인지 확인해 주세요.");
        var words = WordAligner.Snap(transcript.Words, silences);
        s.WordsJson = DraftLogic.Serialize(words);
        s.SttModel = transcript.Model;
        s.SttRuntime = transcript.Runtime;
        s.SttElapsedMs = transcript.ElapsedMs;

        var cuts = CutPlanner.Plan(s.DurationMs, silences, _o.Edit.MinSilenceMs, _o.Edit.KeepPadMs);
        var cues = SubtitleBuilder.Build(words, _o.Edit.MaxLineChars, _o.Edit.MaxLines, _o.Edit.CueGapMs)
            .Select(c => c with { Text = SubtitleBuilder.Rewrap(TermFixer.Apply(c.Text, _o.Terms), _o.Edit.MaxLineChars) })
            .ToList();
        d.CutsJson = DraftLogic.Serialize(cuts);
        d.CuesJson = DraftLogic.Serialize(cues);

        await StageAsync(db, d, "제목·훅 초안 만드는 중", ct);
        // 제목 초안은 마침표가 남아 있는 받아쓰기 원문으로(자막은 마침표를 지워 문장을 나눌 수 없음)
        var spoken = TermFixer.Apply(transcript.Text, _o.Terms);
        var copy = await copywriter.DraftAsync(spoken, ct);
        d.HookCandidatesJson = DraftLogic.Serialize(copy.Hooks);
        d.Hook = copy.Hooks.FirstOrDefault() ?? "";
        d.TitleCandidatesJson = DraftLogic.Serialize(copy.Titles);
        d.Title = copy.Titles.FirstOrDefault() ?? "";
        d.Summary = copy.Summary;
        d.Hashtags = string.Join(' ', copy.Hashtags);
        d.Description = DescriptionBuilder.Build(_o.Copy.DescriptionTemplate, copy.Summary, d.Code, _o.Copy.LandingUrl, copy.Hashtags);
        d.EmphasisJson = DraftLogic.Serialize(copy.Emphasis);
        d.AiNote = copy.AiNote;

        await StageAsync(db, d, "표현 점검 중", ct);
        var risks = await copywriter.ReviewAsync(DraftLogic.AllText(d), ct);
        await DraftLogic.RecheckAsync(db, d, risks, ct);

        d.Status = DraftStatus.Rendering;
        d.Stage = "미리보기 만드는 중";
        await db.SaveChangesAsync(ct);

        await RenderAsync(db, d, preview: true, ct);
        log.LogInformation("초안 {Code} 분석 완료: {Duration}초 → {Output}초, 자막 {Cues}장, 자른 곳 {Cuts}곳, 받아쓰기 {Elapsed}ms ({Runtime})",
            d.Code, s.DurationMs / 1000, d.OutputDurationMs / 1000, cues.Count, cuts.Count, transcript.ElapsedMs, transcript.Runtime);
    }

    /// <summary>
    /// 지금 초안 내용으로 영상을 만듭니다. 만드는 동안 누가 초안을 고쳤으면(Version 변경) 결과를 버립니다 —
    /// 고친 쪽이 이미 새 작업을 넣었으므로 그 작업이 최신 내용으로 다시 만듭니다.
    /// </summary>
    private async Task RenderAsync(AppDbContext db, ShortDraft d, bool preview, CancellationToken ct)
    {
        var s = d.Source;
        var folder = paths.SourceFolder(s.Id);
        var (ass, duration, keeps) = DraftLogic.BuildAss(d, s, _o.Render.EndCardText, _o.Render.EndCardSeconds, _o.Render.FontName);
        if (duration < 1000) throw new InvalidDataException("자르고 남은 영상이 1초보다 짧습니다. 자르기 구간을 확인해 주세요.");

        await File.WriteAllTextAsync(Path.Combine(folder, DraftFiles.Ass), ass, ct);
        var output = Path.Combine(folder, preview ? DraftFiles.Preview : DraftFiles.Final);
        var temp = output + ".part.mp4";
        await video.RenderAsync(new RenderRequest(Path.Combine(folder, s.StoredName), temp, folder, DraftFiles.Ass, keeps, preview), ct);

        d.OutputDurationMs = duration;
        d.Stage = null;
        d.Error = null;
        if (preview)
        {
            d.Status = DraftStatus.Review;
            d.PreviewVersion++;
        }
        else
        {
            d.Status = DraftStatus.Ready;
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            TryDelete(temp);
            return;
        }
        File.Move(temp, output, overwrite: true);
    }

    private static async Task StageAsync(AppDbContext db, ShortDraft d, string stage, CancellationToken ct)
    {
        d.Stage = stage;
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkFailedAsync(int draftId, string message)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var d = await db.Drafts.FirstOrDefaultAsync(x => x.Id == draftId);
        if (d is null) return;
        d.Status = DraftStatus.Failed;
        d.Stage = null;
        d.Error = message.Length > 500 ? message[..500] : message;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // 그사이 고쳐짐 → 새 작업이 다시 처리
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
