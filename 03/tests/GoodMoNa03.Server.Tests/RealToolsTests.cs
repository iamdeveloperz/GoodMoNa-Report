using System.Net.Http.Json;
using System.Diagnostics;
using GoodMoNa03.Server.Editing;
using GoodMoNa03.Server.Infrastructure;
using GoodMoNa03.Server.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GoodMoNa03.Server.Tests;

/// <summary>실제 ffmpeg가 있어야 하는 시험. 평소에는 건너뛰고, GOODMONA03_REAL=1일 때만 돕니다.</summary>
public sealed class RealToolsFactAttribute : FactAttribute
{
    public RealToolsFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GOODMONA03_REAL") != "1")
            Skip = "실제 ffmpeg 시험: GOODMONA03_REAL=1 일 때만 (서버 PC에 ffmpeg 설치 필요)";
    }
}

public sealed class RealToolsTests : IDisposable
{
    /// <summary>GOODMONA03_REAL_OUT을 주면 결과 영상을 그 폴더에 남깁니다(눈으로 자막 확인용).</summary>
    private static readonly string? KeepDir = Environment.GetEnvironmentVariable("GOODMONA03_REAL_OUT") is { Length: > 0 } k ? k : null;

    private readonly string _dir = KeepDir ?? Path.Combine(Path.GetTempPath(), "goodmona03-real", Guid.NewGuid().ToString("N"));
    private readonly AppOptions _options = new();

    public RealToolsTests()
    {
        Directory.CreateDirectory(_dir);
        if (Environment.GetEnvironmentVariable("GOODMONA03_FFMPEG_DIR") is { Length: > 0 } dir)
        {
            _options.Ffmpeg.FfmpegPath = Path.Combine(dir, "ffmpeg.exe");
            _options.Ffmpeg.FfprobePath = Path.Combine(dir, "ffprobe.exe");
        }
    }

    public void Dispose()
    {
        if (KeepDir is not null) return;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// 가짜 촬영본: 1080×1920 테스트 화면 10초 + 소리(0~1초 무음, 1~4초 삐, 4~5.5초 무음, 5.5~9.5초 삐, 9.5~10초 무음).
    /// 실제 환자·원장 영상 대신 ffmpeg가 만든 화면·소리만 씁니다.
    /// </summary>
    private async Task<string> MakeSourceAsync()
    {
        var path = Path.Combine(_dir, "source.mp4");
        var psi = new ProcessStartInfo(_options.Ffmpeg.FfmpegPath) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[]
        {
            "-hide_banner", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=1080x1920:rate=30:duration=10",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=10",
            "-filter_complex", "[1:a]volume='if(between(t,1,4)+between(t,5.5,9.5),0.5,0)':eval=frame[a]",
            "-map", "0:v", "-map", "[a]", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", path,
        }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, err);
        return path;
    }

    /// <summary>
    /// 실제 음성이 든 촬영본으로 전체 흐름(ffmpeg + Whisper + 자막 + 렌더). GOODMONA03_REAL_SOURCE에 영상 경로를 주면 돕니다.
    /// 결과 자막은 cues.txt로 남깁니다(GOODMONA03_REAL_OUT과 함께 쓰면 눈으로 확인 가능).
    /// </summary>
    [RealToolsFact]
    public async Task 실제_음성_촬영본으로_받아쓰기부터_미리보기까지()
    {
        var source = Environment.GetEnvironmentVariable("GOODMONA03_REAL_SOURCE");
        if (string.IsNullOrEmpty(source) || !File.Exists(_options.Whisper.ModelPath)) return; // 음성 촬영본·모델이 없으면 이 시험만 생략

        var tool = new FfmpegVideoTool(Options.Create(_options), NullLogger<FfmpegVideoTool>.Instance);
        using var stt = new WhisperSpeechToText(Options.Create(_options), NullLogger<WhisperSpeechToText>.Instance);
        var e = _options.Edit;

        var probe = await tool.ProbeAsync(source, CancellationToken.None);
        var silences = await tool.DetectSilenceAsync(source, e.SilenceNoiseDb, e.MinSilenceMs, CancellationToken.None);
        var wav = Path.Combine(_dir, "speech.wav");
        await tool.ExtractAudioAsync(source, wav, CancellationToken.None);
        var transcript = await stt.TranscribeAsync(wav, CancellationToken.None);

        var cuts = CutPlanner.Plan(probe.DurationMs, silences, e.MinSilenceMs, e.KeepPadMs);
        var keeps = CutPlanner.Keeps(probe.DurationMs, cuts);
        var words = WordAligner.Snap(transcript.Words, silences);
        var cues = SubtitleBuilder.Build(words, e.MaxLineChars, e.MaxLines, e.CueGapMs)
            .Select(c => c with { Text = SubtitleBuilder.Rewrap(TermFixer.Apply(c.Text, _options.Terms), e.MaxLineChars) }).ToList();
        var output = cues.Select(c => TimeMap.ToOutput(c.StartMs, c.EndMs, keeps) is { } t ? new AssWriter.OutputCue(t.StartMs, t.EndMs, c.Text) : null)
            .OfType<AssWriter.OutputCue>().ToList();
        var duration = CutPlanner.OutputDuration(keeps);

        await File.WriteAllLinesAsync(Path.Combine(_dir, "cues.txt"),
            [$"받아쓰기 {transcript.ElapsedMs}ms ({transcript.Runtime}) · 원본 {probe.DurationMs}ms → {duration}ms · 자른 곳 {cuts.Count}",
             transcript.Text, "",
             .. cues.Select(c => $"{c.StartMs,6}~{c.EndMs,6}  {c.Text.Replace("\n", " / ")}"),
             "", "무음 " + string.Join(", ", silences.Select(s => $"{s.StartMs}~{(s.EndMs == int.MaxValue ? "끝" : s.EndMs)}")),
             "", .. transcript.Words.Zip(words, (raw, w) => $"{raw.StartMs,6}~{raw.EndMs,6} → {w.StartMs,6}~{w.EndMs,6}  {w.Text}")]);

        Assert.NotEmpty(transcript.Words);
        Assert.NotEmpty(output);
        Assert.All(transcript.Words, w => Assert.True(w.EndMs >= w.StartMs && w.StartMs >= 0));

        await File.WriteAllTextAsync(Path.Combine(_dir, "sub.ass"),
            AssWriter.Write(output, "바르기 전 필수", _options.Render.EndCardText, "S1116A", duration, 2.5, _options.Render.FontName, []));
        var preview = Path.Combine(_dir, "speech-preview.mp4");
        await tool.RenderAsync(new RenderRequest(source, preview, _dir, "sub.ass", keeps, Preview: true), CancellationToken.None);
        Assert.InRange((await tool.ProbeAsync(preview, CancellationToken.None)).DurationMs, duration - 200, duration + 200);
    }

    /// <summary>
    /// 서버 전체 흐름을 실제 도구로: 직원 업로드 → (ffmpeg·Whisper·AI) 자동 초안 → 점검 해소 → 원장 승인 → 완성본.
    /// GOODMONA03_REAL_SOURCE 영상으로 돌고, GOODMONA03_REAL_AI_MODEL을 주면 서버 PC Ollama로 제목·훅 초안도 받습니다.
    /// 결과(draft.txt · preview.mp4 · final.mp4)는 GOODMONA03_REAL_OUT에 남깁니다.
    /// </summary>
    [RealToolsFact]
    public async Task 실제_도구로_서버_전체_흐름_업로드부터_완성본까지()
    {
        var source = Environment.GetEnvironmentVariable("GOODMONA03_REAL_SOURCE");
        if (string.IsNullOrEmpty(source) || !File.Exists(_options.Whisper.ModelPath)) return;

        using var app = new TestApp { RealTools = true, Ai = null };
        app.Settings["App:Ffmpeg:FfmpegPath"] = _options.Ffmpeg.FfmpegPath;
        app.Settings["App:Ffmpeg:FfprobePath"] = _options.Ffmpeg.FfprobePath;
        if (Environment.GetEnvironmentVariable("GOODMONA03_REAL_AI_MODEL") is { Length: > 0 } model)
        {
            app.Settings["App:Ai:Mode"] = "Ollama";
            app.Settings["App:Ai:Model"] = model;
        }
        var staff = app.Staff;
        var sw = Stopwatch.StartNew();

        await using var file = File.OpenRead(source);
        var content = new StreamContent(file);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
        var uploaded = await TestApp.Read<Contracts.DraftDto>(await staff.PostAsync($"/api/shorts/sources?name={Path.GetFileName(source)}", content));
        var d = await TestApp.WaitAsync(staff, uploaded.Id, maxPolls: 12_000); // 최대 5분
        var analyzeSeconds = sw.Elapsed.TotalSeconds;
        Assert.True(d.Status == Data.DraftStatus.Review, $"{d.Status}: {d.Error}");

        var report = new List<string>
        {
            $"분석+미리보기 {analyzeSeconds:0.0}초 · 원본 {d.Source.DurationMs / 1000.0:0.0}초 {d.Source.Width}×{d.Source.Height} → 편집 {d.OutputDurationMs / 1000.0:0.0}초 · 받아쓰기 {d.Source.SttElapsedMs}ms ({d.Source.SttRuntime})",
            $"코드 {d.Code} · AI {(d.AiNote ?? "사용")}",
            $"제목: {d.Title}", $"제목 후보: {string.Join(" | ", d.TitleCandidates)}",
            $"훅: {d.Hook}", $"훅 후보: {string.Join(" | ", d.HookCandidates)}",
            $"강조어: {string.Join(", ", d.Emphasis)} · 해시태그: {d.Hashtags}",
            "", "[설명]", d.Description,
            "", $"[자른 곳 {d.Cuts.Count}]", string.Join(", ", d.Cuts.Select(c => $"{c.StartMs / 1000.0:0.0}~{c.EndMs / 1000.0:0.0}")),
            "", $"[자막 {d.Cues.Count}장]",
        };
        report.AddRange(d.Cues.Select(c => $"{c.StartMs / 1000.0,5:0.0}~{c.EndMs / 1000.0,5:0.0}  {c.Text.Replace("\n", " / ")}"));
        report.Add("");
        report.Add($"[표현 점검 {d.Findings.Count}건]");
        report.AddRange(d.Findings.Select(f => $"{(f.Blocking ? "■" : "□")} {f.Source}/{f.Rule} @{f.Where}: {f.Excerpt} — {f.Reason}"));
        report.Add("");
        report.Add("[승인 조건] " + (d.ApprovalBlockers.Count == 0 ? "없음" : string.Join(" ", d.ApprovalBlockers)));

        // 점검을 모두 해소 → 원장 승인 → 완성본
        foreach (var f in d.Findings.Where(f => f.Blocking))
            d = await TestApp.Read<Contracts.DraftDto>(await staff.PostAsJsonAsync($"/api/shorts/drafts/{d.Id}/findings/{f.Id}/resolve", new { note = "시험: 해소" }));
        await TestApp.Read<Contracts.DraftDto>(await app.Doctor.PostAsJsonAsync($"/api/shorts/drafts/{d.Id}/approve", new { version = d.Version }));
        sw.Restart();
        d = await TestApp.WaitAsync(staff, d.Id, maxPolls: 12_000);
        Assert.Equal(Data.DraftStatus.Ready, d.Status);
        report.Insert(1, $"완성본 {sw.Elapsed.TotalSeconds:0.0}초");

        foreach (var name in new[] { "preview.mp4", "final.mp4" })
        {
            var res = await staff.GetAsync($"/api/shorts/drafts/{d.Id}/{name}");
            Assert.True(res.IsSuccessStatusCode);
            await File.WriteAllBytesAsync(Path.Combine(_dir, "e2e-" + name), await res.Content.ReadAsByteArrayAsync());
        }
        await File.WriteAllLinesAsync(Path.Combine(_dir, "draft.txt"), report);
    }

    [RealToolsFact]
    public async Task 실제_ffmpeg로_정보_무음_음성추출_렌더까지()
    {
        var tool = new FfmpegVideoTool(Options.Create(_options), NullLogger<FfmpegVideoTool>.Instance);
        Assert.NotNull(await tool.VersionAsync(CancellationToken.None));
        var source = await MakeSourceAsync();

        var probe = await tool.ProbeAsync(source, CancellationToken.None);
        Assert.Equal(1080, probe.Width);
        Assert.Equal(1920, probe.Height);
        Assert.True(probe.HasAudio);
        Assert.InRange(probe.DurationMs, 9_900, 10_100);

        var silences = await tool.DetectSilenceAsync(source, -35, 400, CancellationToken.None);
        Assert.Contains(silences, s => Math.Abs(s.StartMs - 4_000) < 150 && Math.Abs(s.EndMs - 5_500) < 150);
        var cuts = CutPlanner.Plan(probe.DurationMs, silences, 400, 150);
        var keeps = CutPlanner.Keeps(probe.DurationMs, cuts);
        Assert.Equal(2, keeps.Count); // 앞·가운데·끝 무음을 자르면 소리 구간 두 개

        var wav = Path.Combine(_dir, "audio.wav");
        await tool.ExtractAudioAsync(source, wav, CancellationToken.None);
        Assert.True(new FileInfo(wav).Length > 16_000 * 2 * 9); // 16kHz 16bit 모노 약 10초

        var duration = CutPlanner.OutputDuration(keeps);
        var ass = AssWriter.Write([new(0, 1_500, "미녹시딜 바르기 전\n꼭 알아야 할 3가지")], "바르기 전 필수", "전체 설명은 아래 관련 영상에서", "S1116A",
            duration, 2.5, _options.Render.FontName, []);
        await File.WriteAllTextAsync(Path.Combine(_dir, "sub.ass"), ass);

        foreach (var preview in new[] { true, false })
        {
            var output = Path.Combine(_dir, preview ? "preview.mp4" : "final.mp4");
            await tool.RenderAsync(new RenderRequest(source, output, _dir, "sub.ass", keeps, preview), CancellationToken.None);
            var result = await tool.ProbeAsync(output, CancellationToken.None);
            Assert.Equal(preview ? 540 : 1080, result.Width);
            Assert.Equal(preview ? 960 : 1920, result.Height);
            Assert.True(result.HasAudio);
            Assert.InRange(result.DurationMs, duration - 200, duration + 200);
        }
    }
}
