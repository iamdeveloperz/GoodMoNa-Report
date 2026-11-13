using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoodMoNa.Server.Contracts;
using GoodMoNa.Server.Data;
using GoodMoNa.Server.Transcription;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GoodMoNa.Server.Tests;

/// <summary>원장 녹음 → 자동 변환 → 스크립트 연결 → 삭제 시 함께 삭제.</summary>
public sealed class NoteFlowTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly byte[] FakeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0xFF, 0xD9];

    private readonly TestApp _app = new();
    private readonly HttpClient _http;

    public NoteFlowTests() => _http = _app.CreateClient();

    public void Dispose() => _app.Dispose();

    /// <summary>16kHz 모노 16bit PCM 무음 WAV</summary>
    internal static byte[] Wav(double seconds, int rate = 16000, short channels = 1)
    {
        var data = (int)(rate * seconds) * channels * 2;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + data); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write(channels); w.Write(rate); w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(data); w.Write(new byte[data]);
        return ms.ToArray();
    }

    /// <summary>환자 등록 → 방문 → 촬영 → 보고까지 마친 방문 id</summary>
    private async Task<(int PatientId, int VisitId)> ReportedVisit(string name = "김민수", string birth = "1988-04-17")
    {
        var p = (await (await _http.PostAsJsonAsync("/api/patients", new { name, birth })).Content.ReadFromJsonAsync<PatientSummaryDto>(Json))!;
        var v = (await (await _http.PostAsync($"/api/patients/{p.Id}/visits/today", null)).Content.ReadFromJsonAsync<VisitDto>(Json))!;
        var t = (await (await _http.PostAsync($"/api/visits/{v.Id}/capture-tokens", null)).Content.ReadFromJsonAsync<CaptureTokenDto>(Json))!;
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(FakeJpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "photo", "photo.jpg");
        (await _http.PostAsync($"/api/capture/{t.Token}/photo", form)).EnsureSuccessStatusCode();
        (await _http.PutAsJsonAsync($"/api/visits/{v.Id}/report", new { progress = "Improved", memo = "정수리 좋아짐" })).EnsureSuccessStatusCode();
        return (p.Id, v.Id);
    }

    private Task<HttpResponseMessage> UploadNote(int visitId, byte[] wav, string source = "InApp")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "audio", "note.wav");
        form.Add(new StringContent(source), "source");
        return _http.PostAsync($"/api/visits/{visitId}/notes", form);
    }

    private async Task<NoteDto[]> Notes(int visitId) => (await _http.GetFromJsonAsync<NoteDto[]>($"/api/visits/{visitId}/notes", Json))!;

    private async Task<NoteDto> WaitFor(int visitId, int noteId, NoteStatus status)
    {
        for (var i = 0; i < 100; i++)
        {
            var n = (await Notes(visitId)).FirstOrDefault(x => x.Id == noteId);
            if (n?.Status == status) return n;
            await Task.Delay(50);
        }
        throw new TimeoutException($"녹음 {noteId}가 {status} 상태가 되지 않았습니다.");
    }

    private async Task<T> Db<T>(Func<AppDbContext, Task<T>> q)
    {
        using var scope = _app.Services.CreateScope();
        return await q(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task 녹음을_올리면_자동으로_변환되어_스크립트가_연결된다()
    {
        var (_, visitId) = await ReportedVisit();

        var res = await UploadNote(visitId, Wav(2.5));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var queued = (await res.Content.ReadFromJsonAsync<NoteDto>(Json))!;
        Assert.Equal(1, queued.Seq);
        Assert.Equal(2500, queued.DurationMs); // 클라이언트 말이 아니라 WAV 크기로 계산

        var done = await WaitFor(visitId, queued.Id, NoteStatus.Transcribed);
        Assert.Equal(_app.Stt.Text, done.Transcript!.Text);
        Assert.Single(done.Transcript.Segments);

        // 같은 방문에 여러 녹음: 순번이 이어진다
        var second = (await (await UploadNote(visitId, Wav(1), "FilePick")).Content.ReadFromJsonAsync<NoteDto>(Json))!;
        Assert.Equal(2, second.Seq);
        Assert.Equal(NoteSource.FilePick, second.Source); // 폼의 source 필드가 저장된다
        await WaitFor(visitId, second.Id, NoteStatus.Transcribed);
        Assert.Equal(2, await Db(db => db.Transcripts.CountAsync()));
    }

    [Fact]
    public async Task 녹음은_Range_요청으로_재생할_수_있다()
    {
        var (_, visitId) = await ReportedVisit();
        var note = (await (await UploadNote(visitId, Wav(1))).Content.ReadFromJsonAsync<NoteDto>(Json))!;

        var req = new HttpRequestMessage(HttpMethod.Get, note.AudioUrl);
        req.Headers.Range = new RangeHeaderValue(0, 99);
        var res = await _http.SendAsync(req);

        Assert.Equal(HttpStatusCode.PartialContent, res.StatusCode);
        Assert.Equal(100, (await res.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task 녹음을_지우면_연결된_스크립트와_파일도_지워진다()
    {
        var (_, visitId) = await ReportedVisit();
        var keep = (await (await UploadNote(visitId, Wav(1))).Content.ReadFromJsonAsync<NoteDto>(Json))!;
        var drop = (await (await UploadNote(visitId, Wav(1))).Content.ReadFromJsonAsync<NoteDto>(Json))!;
        await WaitFor(visitId, keep.Id, NoteStatus.Transcribed);
        await WaitFor(visitId, drop.Id, NoteStatus.Transcribed);
        var dropPath = await Db(db => db.VoiceNotes.Where(n => n.Id == drop.Id).Select(n => n.StoragePath).FirstAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/notes/{drop.Id}")).StatusCode);

        var left = await Notes(visitId);
        Assert.Equal([keep.Id], left.Select(n => n.Id));
        Assert.Equal(1, await Db(db => db.Transcripts.CountAsync()));
        Assert.False(await Db(db => db.Transcripts.AnyAsync(t => t.VoiceNoteId == drop.Id)));
        Assert.False(File.Exists(Path.Combine(_app.DataRoot, dropPath)));
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(drop.AudioUrl)).StatusCode);

        // 같은 삭제를 두 번 보내도 안전
        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/notes/{drop.Id}")).StatusCode);
    }

    [Fact]
    public async Task 변환_도중에_지우면_작업이_멈추고_스크립트가_생기지_않는다()
    {
        var (_, visitId) = await ReportedVisit();
        _app.Stt.Hold();
        var note = (await (await UploadNote(visitId, Wav(1))).Content.ReadFromJsonAsync<NoteDto>(Json))!;
        await _app.Stt.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var path = Path.Combine(_app.DataRoot, await Db(db => db.VoiceNotes.Where(n => n.Id == note.Id).Select(n => n.StoragePath).FirstAsync()));

        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);

        // 변환기가 파일을 잡고 있었으므로, 워커가 취소를 받고 파일을 정리할 때까지 기다립니다.
        for (var i = 0; i < 100 && File.Exists(path); i++) await Task.Delay(50);
        Assert.False(File.Exists(path));
        Assert.Empty(await Notes(visitId));
        Assert.Equal(0, await Db(db => db.Transcripts.CountAsync()));
    }

    [Fact]
    public async Task 변환에_실패하면_Failed가_되고_다시_시도할_수_있다()
    {
        var (_, visitId) = await ReportedVisit();
        _app.Stt.Fail = true;
        var note = (await (await UploadNote(visitId, Wav(1))).Content.ReadFromJsonAsync<NoteDto>(Json))!;
        var failed = await WaitFor(visitId, note.Id, NoteStatus.Failed);
        Assert.Contains("가짜 변환 실패", failed.Error);

        _app.Stt.Fail = false;
        Assert.Equal(HttpStatusCode.Accepted, (await _http.PostAsync($"/api/notes/{note.Id}/retry", null)).StatusCode);
        var done = await WaitFor(visitId, note.Id, NoteStatus.Transcribed);
        Assert.Null(done.Error);
        Assert.NotNull(done.Transcript);
    }

    [Theory]
    [InlineData(8000, 1)]
    [InlineData(16000, 2)]
    public async Task Whisper_입력_형식이_아니면_415(int rate, short channels)
    {
        var (_, visitId) = await ReportedVisit();
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await UploadNote(visitId, Wav(1, rate, channels))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await UploadNote(visitId, FakeJpeg)).StatusCode);
    }

    [Fact]
    public async Task 보고_전_방문에는_녹음할_수_없다()
    {
        var p = (await (await _http.PostAsJsonAsync("/api/patients", new { name = "이서연", birth = "920708" })).Content.ReadFromJsonAsync<PatientSummaryDto>(Json))!;
        var v = (await (await _http.PostAsync($"/api/patients/{p.Id}/visits/today", null)).Content.ReadFromJsonAsync<VisitDto>(Json))!;

        Assert.Equal(HttpStatusCode.Conflict, (await UploadNote(v.Id, Wav(1))).StatusCode);
    }

    [Fact]
    public async Task 원장_목록과_환자_기록에_보고와_녹음이_모인다()
    {
        var (patientId, visitId) = await ReportedVisit();
        var note = (await (await UploadNote(visitId, Wav(1))).Content.ReadFromJsonAsync<NoteDto>(Json))!;
        await WaitFor(visitId, note.Id, NoteStatus.Transcribed);

        var list = (await _http.GetFromJsonAsync<ReportListItemDto[]>("/api/director/reports", Json))!;
        var item = Assert.Single(list);
        Assert.Equal(visitId, item.VisitId);
        Assert.Equal(Progress.Improved, item.Progress);
        Assert.Equal(1, item.NoteCount);
        Assert.Equal(0, item.PendingNoteCount);

        Assert.Empty((await _http.GetFromJsonAsync<ReportListItemDto[]>("/api/director/reports?date=2026-11-12", Json))!);

        // 3주 뒤 다시 와서 보고 → 기록에는 두 방문이 최근순으로, 첫 방문의 스크립트까지 들어 있다
        _app.Time.Advance(TimeSpan.FromDays(21));
        var next = (await (await _http.PostAsync($"/api/patients/{patientId}/visits/today", null)).Content.ReadFromJsonAsync<VisitDto>(Json))!;
        var history = (await _http.GetFromJsonAsync<PatientHistoryDto>($"/api/patients/{patientId}/history", Json))!;
        Assert.Equal([next.Id, visitId], history.Visits.Select(v => v.VisitId));
        Assert.Equal(_app.Stt.Text, history.Visits[1].Notes.Single().Transcript!.Text);
    }

    [Theory]
    [InlineData("/", "index")]
    [InlineData("/report", "report")]
    [InlineData("/director", "director")]
    [InlineData("/capture/abc", "capture")]
    public async Task 화면_경로가_각각의_페이지를_연다(string path, string page)
    {
        var res = await _http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains($"<title>{page}</title>", await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("시청해 주셔서 감사합니다.", true)]
    [InlineData("구독과 좋아요 부탁드립니다", false)]
    [InlineData("...", true)]
    [InlineData("정수리 밀도가 좋아졌습니다.", false)]
    public void Whisper_환각_문구를_거른다(string text, bool expected) =>
        Assert.Equal(expected, WhisperSpeechToText.IsHallucination(text));

    [Fact]
    public void WAV_머리말에서_길이를_계산한다()
    {
        Assert.True(WavInfo.TryRead(Wav(3.2), out var info));
        Assert.True(info.IsWhisperReady);
        Assert.Equal(3200, info.DurationMs);
        Assert.False(WavInfo.TryRead(FakeJpeg, out _));
    }
}
