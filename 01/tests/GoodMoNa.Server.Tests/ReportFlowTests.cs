using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoodMoNa.Server.Contracts;
using GoodMoNa.Server.Data;

namespace GoodMoNa.Server.Tests;

public sealed class ReportFlowTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly byte[] FakeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0xFF, 0xD9];

    private readonly TestApp _app = new();
    private readonly HttpClient _http;

    public ReportFlowTests() => _http = _app.CreateClient();

    public void Dispose() => _app.Dispose();

    private async Task<T> Read<T>(HttpResponseMessage res) => (await res.Content.ReadFromJsonAsync<T>(Json))!;

    private async Task<PatientSummaryDto> Register(string name, string birth)
    {
        var res = await _http.PostAsJsonAsync("/api/patients", new { name, birth });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await Read<PatientSummaryDto>(res);
    }

    private async Task<VisitDto> OpenToday(int patientId) =>
        await Read<VisitDto>(await _http.PostAsync($"/api/patients/{patientId}/visits/today", null));

    private async Task<CaptureTokenDto> IssueToken(int visitId)
    {
        var res = await _http.PostAsync($"/api/visits/{visitId}/capture-tokens", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await Read<CaptureTokenDto>(res);
    }

    private Task<HttpResponseMessage> Upload(string token, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "photo", "photo.jpg");
        return _http.PostAsync($"/api/capture/{token}/photo", form);
    }

    [Fact]
    public async Task 등록한_환자는_이름_일부로_검색되고_동명이인은_생년월일순이다()
    {
        await Register("김민수", "1995-11-03");
        await Register("김민수", "880417");
        await Register("이서연", "1992.07.08");

        var list = await _http.GetFromJsonAsync<PatientSummaryDto[]>("/api/patients?q=김민", Json);

        Assert.NotNull(list);
        Assert.Equal(2, list.Length);
        Assert.Equal(new DateOnly(1988, 4, 17), list[0].BirthDate);
        Assert.Equal(new DateOnly(1995, 11, 3), list[1].BirthDate);
    }

    [Fact]
    public async Task 같은_이름과_생년월일은_409와_함께_기존_환자를_알려준다()
    {
        var first = await Register("김민수", "1988-04-17");

        var res = await _http.PostAsJsonAsync("/api/patients", new { name = " 김민수 ", birth = "19880417" });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(first.Id, doc.RootElement.GetProperty("existing").GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("", "1990-01-01")]
    [InlineData("김민수", "1990-13-01")]
    [InlineData("김민수", "2099-01-01")]
    [InlineData("김민수", "abc")]
    public async Task 잘못된_입력은_400(string name, string birth)
    {
        var res = await _http.PostAsJsonAsync("/api/patients", new { name, birth });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task 오늘_방문은_여러번_열어도_하나다()
    {
        var p = await Register("김민지", "1990-02-14");

        var a = await OpenToday(p.Id);
        var b = await OpenToday(p.Id);

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(new DateOnly(2026, 11, 13), a.VisitDate);
        Assert.Equal(VisitStatus.Draft, a.Status);
        Assert.Null(a.Previous);
    }

    [Fact]
    public async Task QR_촬영부터_보고까지_전체_흐름()
    {
        var p = await Register("김민수", "1988-04-17");
        var visit = await OpenToday(p.Id);
        var token = await IssueToken(visit.Id);
        Assert.StartsWith("https://report.example.test/capture/", token.CaptureUrl);

        // 폰: 가린 이름만 보인다
        var info = await _http.GetFromJsonAsync<CaptureInfoDto>($"/api/capture/{token.Token}", Json);
        Assert.Equal("김*수", info!.MaskedName);
        Assert.Equal(CaptureState.Ready, info.State);

        // 사진 없이 보고하면 거절
        var early = await _http.PutAsJsonAsync($"/api/visits/{visit.Id}/report", new { progress = "Improved", memo = "" });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);

        // 폰에서 업로드 → 방문에 사진이 붙고, 파일로 내려받을 수 있다
        var up = await Upload(token.Token, FakeJpeg);
        Assert.Equal(HttpStatusCode.Created, up.StatusCode);
        var photo = await Read<PhotoDto>(up);
        var bytes = await _http.GetByteArrayAsync(photo.Url);
        Assert.Equal(FakeJpeg, bytes);

        // 보고
        var rep = await _http.PutAsJsonAsync($"/api/visits/{visit.Id}/report", new { progress = "Improved", memo = "  정수리 밀도 좋아짐  " });
        Assert.Equal(HttpStatusCode.OK, rep.StatusCode);
        var reported = await Read<VisitDto>(rep);
        Assert.Equal(VisitStatus.Reported, reported.Status);
        Assert.Equal(Progress.Improved, reported.Progress);
        Assert.Equal("정수리 밀도 좋아짐", reported.Memo);
        Assert.Equal(photo.Id, reported.Photo!.Id);

        // 보고 후에는 같은 QR로 더 못 올리고, 두 번 보고도 안 된다
        Assert.Equal(HttpStatusCode.Conflict, (await Upload(token.Token, FakeJpeg)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PutAsJsonAsync($"/api/visits/{visit.Id}/report", new { progress = "Worsened" })).StatusCode);

        // 다음 내원(21일 뒤)에는 오늘 사진이 "직전" 사진으로 나온다
        _app.Time.Advance(TimeSpan.FromDays(21));
        var next = await OpenToday(p.Id);
        Assert.NotEqual(visit.Id, next.Id);
        Assert.Equal(photo.Id, next.Previous!.Photo.Id);
        Assert.Equal(21, next.Previous.DaysAgo);
    }

    [Fact]
    public async Task QR은_10분이_지나면_만료된다()
    {
        var p = await Register("박지훈", "1979-01-25");
        var visit = await OpenToday(p.Id);
        var token = await IssueToken(visit.Id);

        _app.Time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        var info = await _http.GetFromJsonAsync<CaptureInfoDto>($"/api/capture/{token.Token}", Json);
        Assert.Equal(CaptureState.Expired, info!.State);
        Assert.Equal(HttpStatusCode.Gone, (await Upload(token.Token, FakeJpeg)).StatusCode);
    }

    [Fact]
    public async Task 새_QR을_띄우면_이전_QR은_바로_무효다()
    {
        var p = await Register("최유진", "2001-09-30");
        var visit = await OpenToday(p.Id);
        var old = await IssueToken(visit.Id);
        var fresh = await IssueToken(visit.Id);

        Assert.Equal(HttpStatusCode.Gone, (await Upload(old.Token, FakeJpeg)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Upload(fresh.Token, FakeJpeg)).StatusCode);
    }

    [Fact]
    public async Task JPEG가_아닌_파일과_가짜_토큰은_거절된다()
    {
        var p = await Register("이서연", "1992-07-08");
        var visit = await OpenToday(p.Id);
        var token = await IssueToken(visit.Id);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Upload(token.Token, "<svg/>"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Upload(new string('A', 43), FakeJpeg)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/capture/../../etc")).StatusCode);
    }
}
