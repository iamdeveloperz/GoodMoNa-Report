using System.Diagnostics;
using System.Text.RegularExpressions;
using GoodMoNa.Server.Infrastructure;
using Microsoft.Extensions.Options;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace GoodMoNa.Server.Transcription;

public sealed record TranscriptSegment(int StartMs, int EndMs, string Text);

public sealed record TranscriptionResult(string Text, IReadOnlyList<TranscriptSegment> Segments, string Model, string Runtime, int ElapsedMs);

/// <summary>음성 파일(16kHz 모노 WAV)을 글로 바꿉니다. 테스트에서는 가짜 구현으로 바꿔 끼웁니다.</summary>
public interface ISpeechToText
{
    Task<TranscriptionResult> TranscribeAsync(string wavPath, CancellationToken ct);

    /// <summary>모델 로드와 GPU 초기화를 미리 해 둡니다. 첫 녹음 변환이 13초 → 1초로 줄어듭니다.</summary>
    Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>whisper.cpp(Whisper.net)로 이 PC에서 직접 변환합니다. 외부로 음성이 나가지 않습니다.</summary>
public sealed partial class WhisperSpeechToText : ISpeechToText, IDisposable
{
    private readonly WhisperOptions _options;
    private readonly ILogger<WhisperSpeechToText> _log;
    private readonly Lazy<WhisperFactory> _factory;
    private string _runtime = "unloaded";

    public WhisperSpeechToText(IOptions<AppOptions> options, ILogger<WhisperSpeechToText> log)
    {
        _options = options.Value.Whisper;
        _log = log;
        // 모델(1.6GB) 로딩은 첫 녹음 때 한 번만 합니다. 서버 시작이 느려지지 않게.
        // PublicationOnly: 모델 파일이 없어 실패해도 예외를 기억하지 않으므로, 파일을 넣으면 재시작 없이 다음 녹음부터 동작합니다.
        // (변환은 워커 하나가 순서대로 하므로 팩토리가 두 번 만들어질 일은 없습니다.)
        _factory = new Lazy<WhisperFactory>(CreateFactory, LazyThreadSafetyMode.PublicationOnly);
    }

    public string ModelName => Path.GetFileNameWithoutExtension(_options.ModelPath).Replace("ggml-", "");

    private WhisperFactory CreateFactory()
    {
        if (!File.Exists(_options.ModelPath))
            throw new FileNotFoundException($"Whisper 모델 파일이 없습니다: {_options.ModelPath}");

        RuntimeOptions.RuntimeLibraryOrder = _options.UseGpu
            ? [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
            : [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];

        var sw = Stopwatch.StartNew();
        var factory = WhisperFactory.FromPath(_options.ModelPath, new WhisperFactoryOptions { UseGpu = _options.UseGpu });
        _runtime = RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";
        _log.LogInformation("Whisper 모델 로드: {Model} · 런타임 {Runtime} · {Elapsed}ms", ModelName, _runtime, sw.ElapsedMilliseconds);
        return factory;
    }

    public async Task WarmUpAsync(CancellationToken ct)
    {
        if (!File.Exists(_options.ModelPath))
        {
            _log.LogWarning("Whisper 모델이 없어 미리 불러오기를 건너뜁니다: {Path}", _options.ModelPath);
            return;
        }
        var sw = Stopwatch.StartNew();
        var factory = _factory.Value;
        await using var processor = factory.CreateBuilder().WithLanguage(_options.Language).Build();
        // 1초 무음을 한 번 돌려 GPU 셰이더 컴파일·메모리 할당을 끝내 둡니다.
        await foreach (var _ in processor.ProcessAsync(new float[16000], ct)) { }
        _log.LogInformation("Whisper 준비 완료 ({Elapsed}ms)", sw.ElapsedMilliseconds);
    }

    public async Task<TranscriptionResult> TranscribeAsync(string wavPath, CancellationToken ct)
    {
        var factory = _factory.Value;
        var sw = Stopwatch.StartNew();

        var builder = factory.CreateBuilder()
            .WithLanguage(_options.Language)
            .WithThreads(_options.Threads > 0 ? _options.Threads : Math.Max(1, Environment.ProcessorCount / 2))
            .WithNoSpeechThreshold(0.6f);
        if (!string.IsNullOrWhiteSpace(_options.Prompt)) builder = builder.WithPrompt(_options.Prompt);

        await using var processor = builder.Build();
        await using var audio = File.OpenRead(wavPath);

        var segments = new List<TranscriptSegment>();
        await foreach (var s in processor.ProcessAsync(audio, ct))
        {
            var text = s.Text.Trim();
            if (text.Length == 0 || IsHallucination(text)) continue;
            segments.Add(new TranscriptSegment((int)s.Start.TotalMilliseconds, (int)s.End.TotalMilliseconds, text));
        }

        var joined = string.Join(" ", segments.Select(s => s.Text));
        return new TranscriptionResult(joined, segments, ModelName, _runtime, (int)sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Whisper는 무음·잡음 구간에서 학습 데이터(유튜브 자막)의 문구를 지어내는 버릇이 있습니다.
    /// 소견 녹음에 나올 리 없는 대표 문구만 걸러냅니다.
    /// </summary>
    public static bool IsHallucination(string text) => HallucinationPattern().IsMatch(text);

    [GeneratedRegex(@"^(시청해\s*주셔서\s*감사합니다|구독과\s*좋아요|좋아요와\s*구독|다음\s*영상에서\s*만나요|MBC\s*뉴스|KBS\s*뉴스|자막\s*제공|[\s.…,!?·]+)[.!…\s]*$", RegexOptions.IgnoreCase)]
    private static partial Regex HallucinationPattern();

    public void Dispose()
    {
        if (_factory.IsValueCreated) _factory.Value.Dispose();
    }
}
