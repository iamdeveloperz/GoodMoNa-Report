using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GoodMoNa.Server.Transcription;

/// <summary>
/// 변환할 녹음 id 대기열. 업로드 API는 넣기만 하고 바로 202로 응답하며,
/// <see cref="TranscriptionWorker"/>가 하나씩 꺼내 처리합니다 (GPU·CPU를 한 작업이 독점하도록 한 번에 하나).
/// </summary>
public sealed class TranscriptionQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();

    public ChannelReader<int> Reader => _channel.Reader;

    public void Enqueue(int noteId) => _channel.Writer.TryWrite(noteId);

    /// <summary>변환 중인 녹음이 지워지면 작업을 바로 멈춥니다.</summary>
    public void Cancel(int noteId)
    {
        if (_running.TryGetValue(noteId, out var cts)) cts.Cancel();
    }

    internal CancellationTokenSource Begin(int noteId, CancellationToken stopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _running[noteId] = cts;
        return cts;
    }

    internal void End(int noteId)
    {
        if (_running.TryRemove(noteId, out var cts)) cts.Dispose();
    }
}
