using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoodMoNa.Server.Contracts;
using GoodMoNa.Server.Data;
using GoodMoNa.Server.Import;
using Microsoft.Extensions.DependencyInjection;

namespace GoodMoNa.Server.Tests;

/// <summary>이전 전산 기록(CSV + 사진) 가져오기.</summary>
public sealed class ImportTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TestApp _app = new();
    private readonly HttpClient _http;

    public ImportTests() => _http = _app.CreateClient();

    public void Dispose() => _app.Dispose();

    /// <summary>파일마다 내용이 다른 가짜 JPEG (SHA-256이 달라지도록)</summary>
    private static byte[] Jpeg(byte tag) => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, tag, 0xFF, 0xD9];

    private async Task PutPhoto(string path, byte[] bytes, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var res = await _http.PutAsync($"/api/import/files/{path}", new ByteArrayContent(bytes));
        Assert.Equal(expected, res.StatusCode);
    }

    private static MultipartFormDataContent Csv(string text, Encoding? encoding = null)
    {
        var bytes = (encoding ?? new UTF8Encoding(false)).GetBytes(text);
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        return new MultipartFormDataContent { { file, "file", "records.csv" } };
    }

    private async Task<ImportPreviewDto> Preview(string text, Encoding? encoding = null)
    {
        var res = await _http.PostAsync("/api/import/preview", Csv(text, encoding));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ImportPreviewDto>(Json))!;
    }

    private async Task<ImportStatusDto> RunAndWait(string text, bool skipInvalid = false)
    {
        var res = await _http.PostAsync($"/api/import/run?skipInvalid={skipInvalid.ToString().ToLowerInvariant()}", Csv(text));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await _app.Services.GetRequiredService<ImportJobs>().Running.WaitAsync(TimeSpan.FromSeconds(20));
        return (await _http.GetFromJsonAsync<ImportStatusDto>("/api/import/status", Json))!;
    }

    private async Task<PatientHistoryDto> HistoryOf(string name)
    {
        var p = (await _http.GetFromJsonAsync<PatientSummaryDto[]>($"/api/patients?q={Uri.EscapeDataString(name)}", Json))!.Single();
        return (await _http.GetFromJsonAsync<PatientHistoryDto>($"/api/patients/{p.Id}/history", Json))!;
    }

    private const string Records =
        "이름,생년월일,방문일,경과,메모,사진,보고자\n" +
        "김민수,1988-04-17,2025-03-02,유지,\"정수리 밀도 낮음, 미녹시딜 시작\",kim/0302.jpg,\n" +
        "김민수,880417,2025.4.6,호전,\"좋아짐\n다음 달 재방문\",0406.jpg;0406_b.jpg,김간호\n" +
        "이서연,1992/07/08,,,,,\n";

    [Fact]
    public async Task 양식은_엑셀에서_한글이_깨지지_않도록_BOM이_붙은_UTF8이다()
    {
        var res = await _http.GetAsync("/api/import/template.csv");
        var bytes = await res.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.StartsWith("이름,생년월일,방문일,경과,메모,사진", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public async Task 미리보기는_아무것도_저장하지_않고_엑셀_행번호로_오류를_알려준다()
    {
        await PutPhoto("kim/0302.jpg", Jpeg(1));
        var preview = await Preview(
            "이름,생년월일,방문일,경과,메모,사진\n" +
            "김민수,1988-04-17,2025-03-02,유지,,kim/0302.jpg\n" +
            "박지훈,19790231,2025-03-02,,,\n" +          // 3행: 없는 날짜
            "최유진,2001-09-30,2027-01-01,,,\n" +        // 4행: 미래 방문일
            "이서연,1992-07-08,2025-03-02,좋음,,\n" +    // 5행: 모르는 경과
            "김민지,1990-02-14,2025-03-02,,,없는사진.jpg\n" + // 6행: 사진 없음
            "\n");

        Assert.Equal(5, preview.Rows);
        Assert.Equal(4, preview.ErrorRows);
        Assert.Equal([3, 4, 5, 6], preview.Errors.Select(e => e.Row));
        Assert.Contains("생년월일", preview.Errors[0].Message);
        Assert.Contains("오늘보다 뒤", preview.Errors[1].Message);
        Assert.Contains("경과", preview.Errors[2].Message);
        Assert.Contains("찾지 못함", preview.Errors[3].Message);
        Assert.Equal((1, 1, 1, 1), (preview.Patients, preview.NewPatients, preview.Visits, preview.Photos));

        Assert.Empty((await _http.GetFromJsonAsync<PatientSummaryDto[]>("/api/patients?q=김민수", Json))!);
    }

    [Fact]
    public async Task 한국어_엑셀이_CP949로_저장한_CSV도_읽는다()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var preview = await Preview("이름,생년월일\r\n김민수,1988-04-17\r\n", Encoding.GetEncoding(949));

        Assert.StartsWith("CP949", preview.Encoding);
        Assert.Equal(0, preview.ErrorRows);
        Assert.Equal(1, preview.NewPatients);
    }

    [Fact]
    public async Task 실행하면_환자_방문_사진이_한번에_들어가고_다시_돌려도_겹치지_않는다()
    {
        await PutPhoto("kim/0302.jpg", Jpeg(1));
        await PutPhoto("0406.jpg", Jpeg(2));
        await PutPhoto("0406_b.jpg", Jpeg(3));

        var first = await RunAndWait(Records);

        Assert.Equal(ImportState.Succeeded, first.State);
        Assert.Equal(new ImportResultDto(2, 2, 0, 3, 0, 0), first.Result);

        var kim = await HistoryOf("김민수");
        Assert.Equal([new DateOnly(2025, 4, 6), new DateOnly(2025, 3, 2)], kim.Visits.Select(v => v.VisitDate));
        var april = kim.Visits[0];
        Assert.Equal((VisitStatus.Reported, Progress.Improved), (april.Status, april.Progress));
        Assert.Equal("좋아짐\n다음 달 재방문", april.Memo);
        Assert.Equal("정수리 밀도 낮음, 미녹시딜 시작", kim.Visits[1].Memo);

        // 사진은 데이터 폴더로 복사돼 일반 사진 주소로 열립니다.
        var photo = await _http.GetAsync(april.Photo!.Url);
        Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
        Assert.Equal(Jpeg(3), await photo.Content.ReadAsByteArrayAsync());

        // 원장 화면의 그날 목록에도 '이전 기록'으로 보입니다.
        var day = (await _http.GetFromJsonAsync<ReportListItemDto[]>("/api/director/reports?date=2025-04-06", Json))!;
        Assert.Equal("김간호", Assert.Single(day).ReportedBy);
        var march = (await _http.GetFromJsonAsync<ReportListItemDto[]>("/api/director/reports?date=2025-03-02", Json))!;
        Assert.Equal(LegacyImporter.DefaultReporter, Assert.Single(march).ReportedBy);

        // 같은 CSV를 다시 돌려도 환자·방문·사진이 늘지 않습니다.
        var again = await RunAndWait(Records);
        Assert.Equal(new ImportResultDto(0, 0, 0, 0, 3, 0), again.Result);
        Assert.Equal(2, (await HistoryOf("김민수")).Visits.Count);
    }

    [Fact]
    public async Task 이미_등록된_환자와_방문은_새로_만들지_않고_빈칸만_채운다()
    {
        (await _http.PostAsJsonAsync("/api/patients", new { name = "이서연", birth = "1992-07-08" })).EnsureSuccessStatusCode();
        await RunAndWait("이름,생년월일,방문일,경과,메모\n이서연,1992-07-08,2025-05-01,,첫 상담\n");

        var preview = await Preview("이름,생년월일,방문일,경과,메모\n이서연,920708,2025-05-01,유지,첫 상담\n");
        Assert.Equal((0, 0), (preview.NewPatients, preview.NewVisits));

        var status = await RunAndWait("이름,생년월일,방문일,경과,메모\n이서연,920708,2025-05-01,유지,첫 상담\n");
        Assert.Equal(new ImportResultDto(0, 0, 1, 0, 0, 0), status.Result);
        var visit = Assert.Single((await HistoryOf("이서연")).Visits);
        Assert.Equal((Progress.Unchanged, "첫 상담"), (visit.Progress!.Value, visit.Memo));
    }

    [Fact]
    public async Task 오류가_있으면_기본은_거절하고_원하면_오류_행만_빼고_가져온다()
    {
        const string csv = "이름,생년월일,방문일\n김민수,1988-04-17,2025-03-02\n박지훈,잘못된날짜,2025-03-02\n";

        var refused = await _http.PostAsync("/api/import/run", Csv(csv));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Empty((await _http.GetFromJsonAsync<PatientSummaryDto[]>("/api/patients?q=김민수", Json))!);

        var status = await RunAndWait(csv, skipInvalid: true);
        Assert.Equal(new ImportResultDto(1, 1, 0, 0, 0, 1), status.Result);
    }

    [Fact]
    public async Task 머리글에_이름과_생년월일이_없으면_가져올_수_없다()
    {
        var preview = await Preview("성함,나이\n김민수,38\n");
        Assert.Equal(1, Assert.Single(preview.Errors).Row);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await _http.PostAsync("/api/import/run?skipInvalid=true", Csv("성함,나이\n김민수,38\n"))).StatusCode);
    }

    [Fact]
    public async Task 사진_올리기는_가져오기_폴더_밖으로_나갈_수_없고_사진이_아니면_거절한다()
    {
        await PutPhoto("..%5C..%5Cevil.jpg", Jpeg(1), HttpStatusCode.BadRequest);
        await PutPhoto("note.txt", Jpeg(1), HttpStatusCode.BadRequest);
        await PutPhoto("heic.jpg", "....ftypheic"u8.ToArray(), HttpStatusCode.UnsupportedMediaType);
        await PutPhoto("2025/03/ok.jpg", Jpeg(9));

        var folder = (await _http.GetFromJsonAsync<ImportFolderDto>("/api/import/files", Json))!;
        Assert.Equal("2025/03/ok.jpg", Assert.Single(folder.Files).Path);
        Assert.False(File.Exists(Path.Combine(_app.DataRoot, "evil.jpg")));

        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync("/api/import/files")).StatusCode);
        Assert.Equal(0, (await _http.GetFromJsonAsync<ImportFolderDto>("/api/import/files", Json))!.Count);
    }

    [Fact]
    public async Task 같은_이름의_사진이_여러_폴더에_있으면_하위_폴더까지_적어야_한다()
    {
        await PutPhoto("a/same.jpg", Jpeg(1));
        await PutPhoto("b/same.jpg", Jpeg(2));

        var ambiguous = await Preview("이름,생년월일,방문일,사진\n김민수,1988-04-17,2025-03-02,same.jpg\n");
        var exact = await Preview("이름,생년월일,방문일,사진\n김민수,1988-04-17,2025-03-02,b\\same.jpg\n");

        Assert.Contains("2개", Assert.Single(ambiguous.Errors).Message);
        Assert.Equal((0, 1), (exact.ErrorRows, exact.Photos));
    }

    [Fact]
    public void CSV는_따옴표_안의_쉼표_줄바꿈_따옴표를_그대로_읽는다()
    {
        var rows = CsvTable.Parse("a,b,c\r\n\"x, y\",\"줄\n바꿈\",\"say \"\"hi\"\"\"\r\n,,\n");

        Assert.Equal(3, rows.Count);
        Assert.Equal(["x, y", "줄\n바꿈", "say \"hi\""], rows[1]);
        Assert.Equal(["", "", ""], rows[2]);
    }

    [Theory]
    [InlineData("2025-03-02", 2025, 3, 2)]
    [InlineData("2025.3.2", 2025, 3, 2)]
    [InlineData("2025/03/02 0:00", 2025, 3, 2)]
    [InlineData("20250302", 2025, 3, 2)]
    public void 날짜는_엑셀이_내보내는_여러_형식을_받는다(string text, int y, int m, int d)
    {
        Assert.True(LegacyImporter.TryParseDate(text, out var date));
        Assert.Equal(new DateOnly(y, m, d), date);
    }

    [Theory]
    [InlineData("2025-02-30")]
    [InlineData("25-03-02")]
    [InlineData("")]
    public void 읽을_수_없는_날짜는_거절한다(string text) => Assert.False(LegacyImporter.TryParseDate(text, out _));
}
