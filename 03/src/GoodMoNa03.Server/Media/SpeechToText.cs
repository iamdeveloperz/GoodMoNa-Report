using System.Diagnostics;
using GoodMoNa03.Server.Editing;
using GoodMoNa03.Server.Infrastructure;
using Microsoft.Extensions.Options;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace GoodMoNa03.Server.Media;

public sealed record WordTranscript(IReadOnlyList<Word> Words, string Model, string Runtime, int ElapsedMs)
{
    public string Text => string.Join(' ', Words.Select(w => w.Text.Trim()));
}

/// <summary>음성(16kHz 모노 WAV) → 단어별 시간이 붙은 받아쓰기. 테스트에서는 가짜로 바꿔 끼웁니다.</summary>
public interface ISpeechToText
{
    Task<WordTranscript> TranscribeAsync(string wavPath, CancellationToken ct);
}

/// <summary>
/// whisper.cpp(Whisper.net)로 이 PC에서 직접 받아씁니다(01과 같은 모델 파일, 외부 전송 없음).
/// 자막 타이밍을 위해 단어 단위로 끊어 받습니다(토큰 시간 + 띄어쓰기 기준 분할 + 최대 길이 1).
/// </summary>
public sealed class WhisperSpeechToText : ISpeechToText, IDisposable
{
    private readonly WhisperOptions _options;
    private readonly ILogger<WhisperSpeechToText> _log;
    private readonly Lazy<WhisperFactory> _factory;
    private string _runtime = "unloaded";

    public WhisperSpeechToText(IOptions<AppOptions> options, ILogger<WhisperSpeechToText> log)
    {
        _options = options.Value.Whisper;
        _log = log;
        // 모델 파일이 없어 실패해도 예외를 기억하지 않음 → 파일을 넣으면 재시작 없이 다음 작업부터 동작
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

    public async Task<WordTranscript> TranscribeAsync(string wavPath, CancellationToken ct)
    {
        var factory = _factory.Value;
        var sw = Stopwatch.StartNew();

        var builder = factory.CreateBuilder()
            .WithLanguage(_options.Language)
            .WithThreads(_options.Threads > 0 ? _options.Threads : Math.Max(1, Environment.ProcessorCount / 2))
            .WithNoSpeechThreshold(0.6f)
            .WithTokenTimestamps()
            .SplitOnWord()
            .WithMaxSegmentLength(1);
        if (!string.IsNullOrWhiteSpace(_options.Prompt)) builder = builder.WithPrompt(_options.Prompt);

        await using var processor = builder.Build();
        await using var audio = File.OpenRead(wavPath);

        var words = new List<Word>();
        await foreach (var s in processor.ProcessAsync(audio, ct))
        {
            var text = s.Text.Trim();
            if (text.Length == 0) continue;
            words.Add(new Word((int)s.Start.TotalMilliseconds, (int)s.End.TotalMilliseconds, text));
        }
        return new WordTranscript(words, ModelName, _runtime, (int)sw.ElapsedMilliseconds);
    }

    public void Dispose()
    {
        if (_factory.IsValueCreated) _factory.Value.Dispose();
    }
}
