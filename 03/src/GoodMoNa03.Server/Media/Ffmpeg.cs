using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GoodMoNa03.Server.Editing;
using GoodMoNa03.Server.Infrastructure;
using Microsoft.Extensions.Options;

namespace GoodMoNa03.Server.Media;

/// <summary>영상 정보. Width·Height는 회전(폰 세로 촬영)을 반영한 화면 기준.</summary>
public sealed record ProbeResult(int DurationMs, int Width, int Height, bool HasAudio)
{
    public bool IsVertical => Height > Width;
}

/// <summary>렌더 요청. Keeps는 원본 기준 남길 구간, AssFile은 WorkDir 안의 자막 파일 이름.</summary>
public sealed record RenderRequest(string SourcePath, string OutputPath, string WorkDir, string AssFile, IReadOnlyList<TimeRange> Keeps, bool Preview);

/// <summary>영상 처리 도구. 실제로는 ffmpeg, 테스트에서는 가짜로 바꿔 끼웁니다.</summary>
public interface IVideoTool
{
    Task<ProbeResult> ProbeAsync(string path, CancellationToken ct);

    /// <summary>음성 인식용 16kHz 모노 WAV.</summary>
    Task ExtractAudioAsync(string sourcePath, string wavPath, CancellationToken ct);

    Task<IReadOnlyList<TimeRange>> DetectSilenceAsync(string sourcePath, int noiseDb, int minSilenceMs, CancellationToken ct);

    Task RenderAsync(RenderRequest request, CancellationToken ct);

    /// <summary>ffmpeg가 설치되어 있는지(설정 화면 안내용).</summary>
    Task<string?> VersionAsync(CancellationToken ct);
}

public sealed class FfmpegException(string message) : Exception(message);

public sealed class FfmpegVideoTool(IOptions<AppOptions> options, ILogger<FfmpegVideoTool> log) : IVideoTool
{
    private readonly AppOptions _o = options.Value;

    public async Task<ProbeResult> ProbeAsync(string path, CancellationToken ct)
    {
        var r = await RunAsync(_o.Ffmpeg.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", path], null, ct);
        if (r.ExitCode != 0) throw new FfmpegException("영상 정보를 읽지 못했습니다. 영상 파일이 맞는지 확인해 주세요. " + Tail(r.StdErr));
        return FfmpegArgs.ParseProbe(r.StdOut);
    }

    public async Task ExtractAudioAsync(string sourcePath, string wavPath, CancellationToken ct)
    {
        var r = await RunAsync(_o.Ffmpeg.FfmpegPath,
            ["-hide_banner", "-y", "-i", sourcePath, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wavPath], null, ct);
        if (r.ExitCode != 0) throw new FfmpegException("음성을 뽑지 못했습니다. " + Tail(r.StdErr));
    }

    public async Task<IReadOnlyList<TimeRange>> DetectSilenceAsync(string sourcePath, int noiseDb, int minSilenceMs, CancellationToken ct)
    {
        // 앞뒤 무음은 짧아도 자르기 위해 감지 기준은 0.2초로 낮게 두고, 가운데 무음 기준(minSilenceMs)은 CutPlanner가 적용합니다.
        var d = Math.Min(minSilenceMs, 200) / 1000.0;
        var r = await RunAsync(_o.Ffmpeg.FfmpegPath,
            ["-hide_banner", "-nostats", "-i", sourcePath, "-vn",
             "-af", string.Create(CultureInfo.InvariantCulture, $"silencedetect=noise={noiseDb}dB:d={d}"), "-f", "null", "-"], null, ct);
        if (r.ExitCode != 0) throw new FfmpegException("무음 구간을 찾지 못했습니다. " + Tail(r.StdErr));
        return FfmpegArgs.ParseSilence(r.StdErr);
    }

    public async Task RenderAsync(RenderRequest request, CancellationToken ct)
    {
        var encoder = _o.Ffmpeg.VideoEncoder;
        var r = await RunAsync(_o.Ffmpeg.FfmpegPath, FfmpegArgs.Render(request, encoder, _o.Render.FontsDir), request.WorkDir, ct);
        if (r.ExitCode != 0 && encoder != "libx264")
        {
            // GPU 인코더가 없거나 바쁘면 CPU로 한 번 더
            log.LogWarning("{Encoder} 인코딩 실패 → libx264로 다시 시도: {Tail}", encoder, Tail(r.StdErr));
            r = await RunAsync(_o.Ffmpeg.FfmpegPath, FfmpegArgs.Render(request, "libx264", _o.Render.FontsDir), request.WorkDir, ct);
        }
        if (r.ExitCode != 0) throw new FfmpegException("영상을 만들지 못했습니다. " + Tail(r.StdErr));
    }

    public async Task<string?> VersionAsync(CancellationToken ct)
    {
        try
        {
            var r = await RunAsync(_o.Ffmpeg.FfmpegPath, ["-hide_banner", "-version"], null, ct);
            return r.ExitCode == 0 ? r.StdOut.Split('\n').FirstOrDefault()?.Trim() : null;
        }
        catch (FfmpegException)
        {
            return null;
        }
    }

    private sealed record Result(int ExitCode, string StdOut, string StdErr);

    private async Task<Result> RunAsync(string exe, IReadOnlyList<string> args, string? workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir is not null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
        }
        catch (Win32Exception)
        {
            throw new FfmpegException($"{Path.GetFileName(exe)}를 찾을 수 없습니다. 서버 PC에 ffmpeg를 설치하고 App:Ffmpeg 경로를 확인해 주세요.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _o.Ffmpeg.TimeoutMinutes)));
        var stdout = p.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = p.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await p.WaitForExitAsync(timeout.Token);
            return new Result(p.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* 이미 끝남 */ }
            if (ct.IsCancellationRequested) throw;
            throw new FfmpegException($"ffmpeg가 {_o.Ffmpeg.TimeoutMinutes}분 안에 끝나지 않았습니다.");
        }
    }

    /// <summary>ffmpeg 오류 출력의 마지막 몇 줄(화면에 보여 줄 만큼).</summary>
    private static string Tail(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = string.Join(" / ", lines.TakeLast(3));
        return tail.Length > 400 ? tail[^400..] : tail;
    }
}

/// <summary>ffmpeg 명령 인자 만들기와 출력 해석. 순수 함수라 ffmpeg 없이 테스트합니다.</summary>
public static partial class FfmpegArgs
{
    public const int Fps = 30;

    public static ProbeResult ParseProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double seconds = 0;
        if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d))
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);

        int width = 0, height = 0;
        var hasAudio = false;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                if (type == "audio") hasAudio = true;
                if (type != "video" || width > 0) continue;
                if (s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1) continue;
                width = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                if (Math.Abs(Rotation(s)) % 180 == 90) (width, height) = (height, width);
            }
        }
        return new ProbeResult((int)Math.Round(seconds * 1000), width, height, hasAudio);
    }

    /// <summary>폰 영상의 회전 정보: 예전 방식(tags.rotate) 또는 새 방식(side_data_list의 rotation).</summary>
    private static int Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var r)
            && int.TryParse(r.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rot))
            return rot;
        if (stream.TryGetProperty("side_data_list", out var list))
            foreach (var sd in list.EnumerateArray())
                if (sd.TryGetProperty("rotation", out var v) && v.ValueKind == JsonValueKind.Number)
                    return (int)v.GetDouble();
        return 0;
    }

    /// <summary>silencedetect 출력(stderr)에서 무음 구간을 읽습니다. 끝나지 않은 무음(영상 끝까지)은 시작만 있음 → 매우 큰 끝값.</summary>
    public static List<TimeRange> ParseSilence(string stderr)
    {
        var result = new List<TimeRange>();
        double? start = null;
        foreach (Match m in SilencePattern().Matches(stderr))
        {
            var value = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (m.Groups[1].Value == "start") start = Math.Max(0, value);
            else if (start is { } s)
            {
                result.Add(new TimeRange((int)Math.Round(s * 1000), (int)Math.Round(value * 1000)));
                start = null;
            }
        }
        if (start is { } open) result.Add(new TimeRange((int)Math.Round(open * 1000), int.MaxValue));
        return result;
    }

    [GeneratedRegex(@"silence_(start|end):\s*(-?[\d.]+)")]
    private static partial Regex SilencePattern();

    /// <summary>
    /// 남길 구간만 이어 붙이고 → 9:16으로 채워 자르고 → 자막을 입히고 → 음량을 맞춥니다.
    /// 자막 파일은 작업 폴더 안의 고정 이름(sub.ass)이라 경로 이스케이프가 필요 없고, 대신 ffmpeg를 WorkDir에서 실행해야 합니다.
    /// </summary>
    public static List<string> Render(RenderRequest r, string encoder, string fontsDir)
    {
        var (w, h) = r.Preview ? (540, 960) : (1080, 1920);
        var select = SelectExpression(r.Keeps);
        var subtitles = $"subtitles=filename={r.AssFile}"
                        + (string.IsNullOrWhiteSpace(fontsDir) ? "" : $":fontsdir={EscapeFilterValue(fontsDir)}");
        var video = string.Create(CultureInfo.InvariantCulture,
            $"[0:v]fps={Fps},select='{select}',setpts=N/{Fps}/TB,scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},setsar=1,{subtitles}[v]");
        var audio = $"[0:a]aselect='{select}',asetpts=N/SR/TB,loudnorm=I=-14:TP=-1.5:LRA=11,aresample=48000[a]";

        var args = new List<string> { "-hide_banner", "-y", "-i", r.SourcePath, "-filter_complex", video + ";" + audio, "-map", "[v]", "-map", "[a]" };
        args.AddRange(encoder switch
        {
            "h264_nvenc" => ["-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", r.Preview ? "30" : "21", "-b:v", "0"],
            _ => ["-c:v", "libx264", "-preset", r.Preview ? "veryfast" : "medium", "-crf", r.Preview ? "28" : "20"],
        });
        args.AddRange(["-pix_fmt", "yuv420p", "-r", Fps.ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac", "-b:a", r.Preview ? "96k" : "160k", "-movflags", "+faststart", r.OutputPath]);
        return args;
    }

    /// <summary>select/aselect 식: between(t,0.000,3.200)+between(t,...). 작은따옴표 안에 넣으므로 쉼표를 이스케이프하지 않습니다.</summary>
    public static string SelectExpression(IReadOnlyList<TimeRange> keeps)
    {
        if (keeps.Count == 0) throw new ArgumentException("남길 구간이 없습니다.", nameof(keeps));
        return string.Join('+', keeps.Select(k => string.Create(CultureInfo.InvariantCulture,
            $"between(t,{k.StartMs / 1000.0:0.000},{k.EndMs / 1000.0:0.000})")));
    }

    /// <summary>필터 옵션의 윈도 경로 값: 역슬래시 → 슬래시, 드라이브 ':' → '\:', 작은따옴표로 감쌈. 예: 'C\:/GoodMoNa/fonts'</summary>
    public static string EscapeFilterValue(string value) =>
        "'" + value.Replace('\\', '/').Replace(":", "\\:") + "'";
}
