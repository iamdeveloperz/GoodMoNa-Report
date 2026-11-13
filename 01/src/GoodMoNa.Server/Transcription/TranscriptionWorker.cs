using GoodMoNa.Server.Data;
using GoodMoNa.Server.Endpoints;
using GoodMoNa.Server.Hubs;
using GoodMoNa.Server.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa.Server.Transcription;

/// <summary>
/// 녹음이 올라오면 자동으로 Whisper 변환을 돌리고, 결과 스크립트를 그 녹음에 1:1로 붙입니다.
/// 상태가 바뀔 때마다 원장 앱에 SignalR로 알립니다 (Queued → Transcribing → Transcribed / Failed).
/// </summary>
public sealed class TranscriptionWorker(
    IServiceScopeFactory scopes,
    TranscriptionQueue queue,
    ISpeechToText stt,
    MediaStorage storage,
    ClinicClock clock,
    IHubContext<VisitHub> hub,
    ILogger<TranscriptionWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        await Task.Yield(); // 서버 시작(요청 받기)을 막지 않도록 바로 넘깁니다.
        try
        {
            await stt.WarmUpAsync(stopping);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Whisper 미리 불러오기 실패 — 첫 녹음 때 다시 시도합니다.");
        }

        await RecoverPendingAsync(stopping);

        await foreach (var noteId in queue.Reader.ReadAllAsync(stopping))
        {
            try
            {
                await ProcessAsync(noteId, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break; // 서버 종료: 진행 중이던 녹음은 다음 시작 때 다시 변환합니다.
            }
            catch (Exception ex)
            {
                log.LogError(ex, "녹음 {NoteId} 처리 중 예상치 못한 오류", noteId);
            }
        }
    }

    /// <summary>서버가 꺼질 때 대기 중이거나 변환 중이던 녹음을 다시 대기열에 넣습니다.</summary>
    private async Task RecoverPendingAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = await db.VoiceNotes
            .Where(n => n.Status == NoteStatus.Queued || n.Status == NoteStatus.Transcribing)
            .OrderBy(n => n.Id).Select(n => n.Id).ToListAsync(ct);
        foreach (var id in ids) queue.Enqueue(id);
        if (ids.Count > 0) log.LogInformation("변환 대기 녹음 {Count}개를 다시 대기열에 넣었습니다.", ids.Count);
    }

    private async Task ProcessAsync(int noteId, CancellationToken stopping)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var note = await db.VoiceNotes.Include(n => n.Transcript).FirstOrDefaultAsync(n => n.Id == noteId, stopping);
        if (note is null || note.Status == NoteStatus.Transcribed) return; // 그새 지워졌거나 이미 끝난 녹음

        note.Status = NoteStatus.Transcribing;
        note.Error = null;
        try
        {
            await db.SaveChangesAsync(stopping);
        }
        catch (DbUpdateConcurrencyException)
        {
            return; // 읽은 직후 지워짐
        }
        await PushAsync(note);

        var path = storage.GetFullPath(note.StoragePath);
        TranscriptionResult result;
        var job = queue.Begin(noteId, stopping);
        try
        {
            result = await stt.TranscribeAsync(path, job.Token);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            // 변환 도중 원장이 녹음을 지운 경우. 파일은 열려 있어서 못 지웠을 수 있으니 여기서 정리합니다.
            storage.TryDelete(note.StoragePath);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "녹음 {NoteId} 변환 실패", noteId);
            await MarkFailedAsync(db, noteId, ex.Message);
            return;
        }
        finally
        {
            queue.End(noteId);
        }

        // 변환하는 동안 지워졌는지 다시 확인합니다. 지워졌다면 스크립트를 붙이지 않고 파일만 정리합니다.
        db.ChangeTracker.Clear();
        var current = await db.VoiceNotes.Include(n => n.Transcript).FirstOrDefaultAsync(n => n.Id == noteId, stopping);
        if (current is null)
        {
            storage.TryDelete(note.StoragePath);
            return;
        }

        if (current.Transcript is not null) db.Transcripts.Remove(current.Transcript); // 재시도로 다시 만든 경우
        current.Transcript = new Transcript
        {
            Text = result.Text,
            SegmentsJson = NoteQueries.SerializeSegments(result.Segments),
            Model = result.Model,
            Runtime = result.Runtime,
            ElapsedMs = result.ElapsedMs,
            CreatedAt = clock.UtcNow
        };
        current.Status = NoteStatus.Transcribed;
        try
        {
            await db.SaveChangesAsync(stopping);
        }
        catch (DbUpdateException)
        {
            // 저장 직전에 지워진 경우 (FK 위반). 스크립트는 버립니다.
            storage.TryDelete(note.StoragePath);
            return;
        }

        log.LogInformation("녹음 {NoteId} 변환 완료: {Duration}초 음성 → {Elapsed}ms ({Runtime})",
            noteId, current.DurationMs / 1000, result.ElapsedMs, result.Runtime);
        await PushAsync(current);
    }

    private async Task MarkFailedAsync(AppDbContext db, int noteId, string message)
    {
        db.ChangeTracker.Clear();
        var n = await db.VoiceNotes.FirstOrDefaultAsync(x => x.Id == noteId);
        if (n is null) return;
        n.Status = NoteStatus.Failed;
        n.Error = message.Length > 500 ? message[..500] : message;
        await db.SaveChangesAsync();
        await PushAsync(n);
    }

    private Task PushAsync(VoiceNote n) =>
        hub.Clients.Groups(VisitHub.DirectorGroup, VisitHub.VisitGroup(n.VisitId))
            .SendAsync(VisitHub.Events.NoteUpdated, NoteQueries.ToDto(n));
}
