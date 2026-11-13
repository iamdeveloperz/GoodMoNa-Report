using GoodMoNa.Server.Contracts;
using GoodMoNa.Server.Data;
using GoodMoNa.Server.Hubs;
using GoodMoNa.Server.Infrastructure;
using GoodMoNa.Server.Transcription;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoodMoNa.Server.Endpoints;

/// <summary>원장 소견 녹음: 올리기 → 자동 변환 → 스크립트 연결 → (필요 없으면) 녹음과 스크립트 함께 삭제.</summary>
public static class NoteEndpoints
{
    public static void MapNoteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/visits/{visitId:int}/notes", List);
        app.MapPost("/api/visits/{visitId:int}/notes", Upload).DisableAntiforgery();
        app.MapGet("/api/notes/{noteId:int}/audio", Audio);
        app.MapDelete("/api/notes/{noteId:int}", Delete);
        app.MapPost("/api/notes/{noteId:int}/retry", Retry);
    }

    private static async Task<IResult> List(int visitId, AppDbContext db, CancellationToken ct)
    {
        if (!await db.Visits.AnyAsync(v => v.Id == visitId, ct)) return Problems.NotFound("방문을 찾을 수 없습니다.");
        var notes = await db.VoiceNotes.AsNoTracking().Include(n => n.Transcript)
            .Where(n => n.VisitId == visitId).OrderBy(n => n.Seq).ToListAsync(ct);
        return Results.Ok(notes.Select(NoteQueries.ToDto));
    }

    private static async Task<IResult> Upload(
        int visitId, IFormFile? audio, string? source, AppDbContext db, MediaStorage storage, ClinicClock clock,
        TranscriptionQueue queue, IOptions<AppOptions> options, IHubContext<VisitHub> hub, CancellationToken ct)
    {
        var visit = await db.Visits.FirstOrDefaultAsync(v => v.Id == visitId, ct);
        if (visit is null) return Problems.NotFound("방문을 찾을 수 없습니다.");
        if (visit.Status != VisitStatus.Reported) return Problems.Conflict("보고가 올라온 방문에만 소견을 녹음할 수 있습니다.");

        if (audio is null || audio.Length == 0) return Problems.Validation("audio", "녹음 파일이 없습니다.");
        var maxMinutes = options.Value.MaxNoteMinutes;
        var maxBytes = 44L + 32_000L * 60 * maxMinutes + 1024;
        if (audio.Length > maxBytes) return Problems.TooLarge($"녹음은 {maxMinutes}분까지 올릴 수 있습니다.");

        using var buffer = new MemoryStream((int)audio.Length);
        await audio.CopyToAsync(buffer, ct);
        var bytes = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);

        if (!WavInfo.TryRead(bytes.Span, out var wav) || !wav.IsWhisperReady)
            return Problems.Unsupported("16kHz 모노 WAV만 받을 수 있습니다. 화면을 새로고침한 뒤 다시 녹음해 주세요.");
        if (wav.DurationMs < 500) return Problems.Validation("audio", "녹음이 너무 짧습니다.");

        var noteSource = Enum.TryParse<NoteSource>(source, ignoreCase: true, out var s) ? s : NoteSource.InApp;
        var now = clock.UtcNow;
        var path = await storage.SaveAsync(bytes, "audio", ".wav", now, ct);

        VoiceNote? note = null;
        for (var attempt = 0; attempt < 3 && note is null; attempt++)
        {
            var seq = (await db.VoiceNotes.Where(n => n.VisitId == visitId).MaxAsync(n => (int?)n.Seq, ct) ?? 0) + 1;
            var candidate = new VoiceNote
            {
                VisitId = visitId, Seq = seq, StoragePath = path, ContentType = "audio/wav", SizeBytes = bytes.Length,
                DurationMs = wav.DurationMs, Source = noteSource, Status = NoteStatus.Queued, CreatedAt = now
            };
            db.VoiceNotes.Add(candidate);
            try
            {
                await db.SaveChangesAsync(ct);
                note = candidate;
            }
            catch (DbUpdateException)
            {
                // 같은 방문에 두 녹음이 동시에 올라와 순번이 겹친 경우: 번호를 다시 매깁니다.
                db.ChangeTracker.Clear();
            }
        }
        if (note is null)
        {
            storage.TryDelete(path);
            return Problems.Conflict("녹음을 저장하지 못했습니다. 다시 시도해 주세요.");
        }

        queue.Enqueue(note.Id);
        var dto = NoteQueries.ToDto(note);
        await hub.Clients.Groups(VisitHub.DirectorGroup, VisitHub.VisitGroup(visitId)).SendAsync(VisitHub.Events.NoteUpdated, dto, ct);
        return Results.Accepted($"/api/notes/{note.Id}", dto);
    }

    private static async Task<IResult> Audio(int noteId, AppDbContext db, MediaStorage storage, HttpResponse response, CancellationToken ct)
    {
        var note = await db.VoiceNotes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null) return Problems.NotFound("녹음을 찾을 수 없습니다.");
        var full = storage.GetFullPath(note.StoragePath);
        if (!File.Exists(full)) return Problems.NotFound("녹음 파일이 없습니다.");

        response.Headers.CacheControl = "private, max-age=31536000, immutable";
        // iPad Safari는 Range 요청이 되어야 <audio>로 재생·탐색합니다.
        return Results.File(full, note.ContentType, enableRangeProcessing: true);
    }

    /// <summary>녹음을 지우면 연결된 스크립트도 함께 지워집니다 (DB cascade). 변환 중이면 작업도 멈춥니다.</summary>
    private static async Task<IResult> Delete(
        int noteId, AppDbContext db, MediaStorage storage, TranscriptionQueue queue, IHubContext<VisitHub> hub, CancellationToken ct)
    {
        var note = await db.VoiceNotes.Include(n => n.Transcript).FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null) return Results.NoContent(); // 이미 지워짐: 같은 요청을 두 번 보내도 안전

        db.VoiceNotes.Remove(note);
        await db.SaveChangesAsync(ct);

        queue.Cancel(noteId);
        storage.TryDelete(note.StoragePath); // 변환 중이라 잠겨 있으면 워커가 끝난 뒤 지웁니다.

        await hub.Clients.Groups(VisitHub.DirectorGroup, VisitHub.VisitGroup(note.VisitId))
            .SendAsync(VisitHub.Events.NoteDeleted, note.VisitId, noteId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Retry(int noteId, AppDbContext db, TranscriptionQueue queue, IHubContext<VisitHub> hub, CancellationToken ct)
    {
        var note = await db.VoiceNotes.Include(n => n.Transcript).FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null) return Problems.NotFound("녹음을 찾을 수 없습니다.");
        if (note.Status != NoteStatus.Failed) return Problems.Conflict("변환에 실패한 녹음만 다시 시도할 수 있습니다.");

        note.Status = NoteStatus.Queued;
        note.Error = null;
        await db.SaveChangesAsync(ct);
        queue.Enqueue(noteId);

        var dto = NoteQueries.ToDto(note);
        await hub.Clients.Groups(VisitHub.DirectorGroup, VisitHub.VisitGroup(note.VisitId)).SendAsync(VisitHub.Events.NoteUpdated, dto, ct);
        return Results.Accepted($"/api/notes/{noteId}", dto);
    }
}
