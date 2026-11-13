using System.Text.Json;
using GoodMoNa.Server.Contracts;
using GoodMoNa.Server.Data;
using GoodMoNa.Server.Transcription;

namespace GoodMoNa.Server.Endpoints;

internal static class NoteQueries
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string SerializeSegments(IEnumerable<TranscriptSegment> segments) =>
        JsonSerializer.Serialize(segments.Select(s => new SegmentDto(s.StartMs, s.EndMs, s.Text)), Json);

    public static NoteDto ToDto(VoiceNote n) => new(
        n.Id,
        n.VisitId,
        n.Seq,
        n.Status,
        n.Error,
        n.DurationMs,
        n.Source,
        n.CreatedAt,
        $"/api/notes/{n.Id}/audio",
        n.Transcript is { } t
            ? new TranscriptDto(t.Text, JsonSerializer.Deserialize<List<SegmentDto>>(t.SegmentsJson, Json) ?? [], t.Model, t.Runtime, t.ElapsedMs, t.CreatedAt)
            : null);
}
