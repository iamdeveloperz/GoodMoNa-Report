using System.Net;
using System.Net.Http.Json;
using GoodMoNa03.Server.Auth;
using GoodMoNa03.Server.Contracts;
using GoodMoNa03.Server.Data;
using GoodMoNa03.Server.Media;
using static GoodMoNa03.Server.Tests.TestApp;

namespace GoodMoNa03.Server.Tests;

/// <summary>촬영본 올리기 → 자동 초안 → 검토(고치기·점검 해소) → 원장 승인 → 완성본 → 게시 기록</summary>
public sealed class FlowTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task 촬영본을_올리면_자막_컷_제목_점검_미리보기가_자동으로_만들어진다()
    {
        var d = await _app.UploadAndWaitAsync();

        Assert.Equal(DraftStatus.Review, d.Status);
        Assert.Equal("S1116A", d.Code); // 2026-11-16 첫 초안
        Assert.Equal(1, d.PreviewVersion);
        Assert.Equal("박마케팅", d.Source.UploadedByName);
        Assert.Equal(20_000, d.Source.DurationMs);

        // 무음: 앞(0~0.8초) · 가운데(6~7초) · 끝(19.5초~) → 세 곳 자름
        Assert.Equal(3, d.Cuts.Count);
        Assert.All(d.Cuts, c => Assert.True(c.Removed));
        Assert.Equal(20_000 - 650 - 700 - 350, d.OutputDurationMs);

        Assert.NotEmpty(d.Cues);
        Assert.All(d.Cues, c => Assert.All(c.Text.Split('\n'), l => Assert.True(l.Length <= 12)));

        // AI 꺼짐 → 첫 문장이 제목, 설명란에 영상 코드
        Assert.Equal("미녹시딜 바르기 전에 꼭 알아야 할 것 세 가지입니다", d.Title);
        Assert.Contains("AI 꺼짐", d.AiNote);
        Assert.Contains("영상 코드 S1116A", d.Description);

        // "부작용 없이" → 규칙 점검에 걸려 승인이 막힘
        Assert.Contains(d.Findings, f => f.Rule == "부작용 부정" && f.Blocking && f.Resolution is null);
        Assert.Contains(d.ApprovalBlockers, b => b.Contains("표현 점검"));

        // 미리보기: 540×960, 남길 구간만, 자막 파일에 끝 화면 영상 코드
        Assert.True(_app.Video.Renders.TryPeek(out var render));
        Assert.True(render!.Preview);
        Assert.Equal(3, _app.Video.Renders.Single().Keeps.Count + 1); // 가운데 한 번 잘려 두 구간
        Assert.Contains("상담 시 영상 코드 S1116A", _app.Video.AssFiles.Single());

        var preview = await _app.Staff.GetAsync($"/api/shorts/drafts/{d.Id}/preview.mp4");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("video/mp4", preview.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AI를_켜면_훅_제목_후보와_AI_점검이_더해진다()
    {
        _app.Ai = new FakeAiWriter();
        var d = await _app.UploadAndWaitAsync();

        Assert.Equal("미녹시딜 3가지", d.Hook);
        Assert.Equal(3, d.HookCandidates.Count);
        Assert.Equal("미녹시딜 바르기 전 꼭 알아야 할 3가지", d.Title);
        Assert.Equal(2, d.TitleCandidates.Count);
        Assert.Contains(d.Findings, f => f.Source == FindingSource.Ai && f.Blocking);
        Assert.Null(d.AiNote);
        Assert.Contains("Hook,,0,0,0,,미녹시딜 3가지", _app.Video.AssFiles.Single());
    }

    [Fact]
    public async Task 점검을_모두_해소해야_원장이_승인하고_승인하면_완성본이_만들어진다()
    {
        var d = await _app.UploadAndWaitAsync();
        var doctor = _app.Doctor;

        var blocked = await doctor.PostAsJsonAsync($"/api/shorts/drafts/{d.Id}/approve", new { version = d.Version });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        var staff = _app.Staff;
        foreach (var f in d.Findings.Where(f => f.Blocking))
        {
            var empty = await staff.PostAsJsonAsync($"/api/shorts/drafts/{d.Id}/findings/{f.Id}/resolve", new { note = "" });
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
            d = await Read<DraftDto>(await staff.PostAsJsonAsync($"/api/shorts/drafts/{d.Id}/findings/{f.Id}/resolve",
                new { note = "효과가 바로 나는 약이 없다는 뜻이라 문제없음" }));
        }
        Assert.Empty(d.ApprovalBlockers);
        Assert.All(d.Findings.Where(f => f.Blocking), f => Assert.Equal("박마케팅", f.ResolvedByName));

        // 직원은 승인할 수 없음(의료광고 책임은 원장)
        var staffApprove = await staff.PostAsJsonAsync($"/api/shorts/drafts/{d.Id}/approve", new { version = d.Version });
        Assert.Equal(HttpStatusCode.Forbidden, staffApprove.StatusCode);

        // 화면이 본 버전이 아니면 거절
        var stale = await doctor.PostAsJsonAsync($"/api/shorts/drafts/{d.Id}/approve", new { version = d.Version - 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var approved = await Read<DraftDto>(await doctor.PostAsJsonAsync($"/api/shorts/drafts/{d.Id}/approve", new { version = d.Version }));
        Assert.Equal("이원장", approved.ApprovedByName);
        d = await WaitAsync(staff, d.Id);
        Assert.Equal(DraftStatus.Ready, d.Status);
        Assert.Contains(_app.Video.Renders, r => !r.Preview);

        var file = await staff.GetAsync($"/api/shorts/drafts/{d.Id}/final.mp4?download=true");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("S1116A.mp4", file.Content.Headers.ContentDisposition?.FileNameStar ?? file.Content.Headers.ContentDisposition?.FileName);

        // 승인 뒤에는 고칠 수 없음
        var edit = await staff.PutAsJsonAsync($"/api/shorts/drafts/{d.Id}", new { version = d.Version, title = "바꾼 제목" });
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
    }

    [Fact]
    public async Task 자막이나_자르기를_고치면_미리보기를_다시_만들고_예전_버전으로는_저장할_수_없다()
    {
        var d = await _app.UploadAndWaitAsync();
        var staff = _app.Staff;

        var edited = await Read<DraftDto>(await staff.PutAsJsonAsync($"/api/shorts/drafts/{d.Id}", new
        {
            version = d.Version,
            cues = new[] { new { id = d.Cues[0].Id, text = "미녹시딜 바르기 전 꼭 알아야 할 것" } },
            cuts = new[] { new { index = 1, removed = false } }, // 가운데 무음은 살림
        }));
        Assert.Equal(DraftStatus.Rendering, edited.Status);
        Assert.Equal("미녹시딜 바르기 전 꼭\n알아야 할 것", edited.Cues[0].Text);
        Assert.Equal("박마케팅", edited.UpdatedByName);

        var stale = await staff.PutAsJsonAsync($"/api/shorts/drafts/{d.Id}", new { version = d.Version, title = "늦은 저장" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        d = await WaitAsync(staff, d.Id);
        Assert.Equal(DraftStatus.Review, d.Status);
        Assert.Equal(2, d.PreviewVersion);
        Assert.Equal(20_000 - 650 - 350, d.OutputDurationMs);
        Assert.Single(_app.Video.Renders.Last().Keeps);

        // 제목만 바꾸면 영상은 다시 만들지 않음
        var titled = await Read<DraftDto>(await staff.PutAsJsonAsync($"/api/shorts/drafts/{d.Id}", new { version = d.Version, title = "  미녹시딜 바르는 순서  " }));
        Assert.Equal(DraftStatus.Review, titled.Status);
        Assert.Equal("미녹시딜 바르는 순서", titled.Title);
    }

    [Fact]
    public async Task 자막_장_경계에_걸친_표현도_잡고_한_장_안의_표현은_두_번_잡지_않는다()
    {
        // 실제 쇼츠에서 놓쳤던 모양: "…치료들만으로도 확실히" / "풍성해질 수 있으니까"가 두 장으로 갈림
        _app.Stt.Words = FakeSpeechToText.Sentence(1_000, "지금 있는 치료들만으로도 확실히", "풍성해질 수 있으니까.", "야 이거 노벨상감인데.");
        var d = await _app.UploadAndWaitAsync();

        Assert.Contains(d.Cues, c => c.Text.Replace("\n", " ").EndsWith("확실히"));
        var boundary = Assert.Single(d.Findings, f => f.Where == "자막 장 경계");
        Assert.Equal("효과 보장", boundary.Rule);
        Assert.Single(d.Findings, f => f.Rule == "과장" && f.Where.StartsWith("자막")); // 한 장 안에 있는 "노벨"은 장별 검사로만 한 번(설명란 요약에서 따로 한 번 더 잡히는 건 정상)
    }

    [Fact]
    public async Task 고친_글에서_문제_표현이_사라지면_점검도_사라지고_새로_쓴_표현은_다시_걸린다()
    {
        var d = await _app.UploadAndWaitAsync();
        var cue = d.Cues.First(c => c.Text.Replace("\n", " ").Contains("부작용"));

        d = await Read<DraftDto>(await _app.Staff.PutAsJsonAsync($"/api/shorts/drafts/{d.Id}", new
        {
            version = d.Version,
            title = "미녹시딜 효과 3배 보는 법",
            cues = new[] { new { id = cue.Id, text = "효과가 바로 나는 약은 없습니다" } },
        }));
        Assert.DoesNotContain(d.Findings, f => f.Rule == "부작용 부정" && f.Where.StartsWith("자막"));
        Assert.Contains(d.Findings, f => f.Rule == "부작용 부정" && f.Where == "설명"); // 설명란 요약에는 아직 남아 있음
        Assert.Contains(d.Findings, f => f.Rule == "근거 수치" && f.Where == "제목");
    }

    [Fact]
    public async Task 환자가_나오는_영상은_출연_동의를_확인해야_승인된다()
    {
        _app.Stt.Words = FakeSpeechToText.Sentence(1_000, "미녹시딜은 하루 두 번 바릅니다.");
        var d = await _app.UploadAndWaitAsync();
        Assert.Empty(d.ApprovalBlockers);

        d = await Read<DraftDto>(await _app.Staff.PutAsJsonAsync($"/api/shorts/drafts/{d.Id}", new { version = d.Version, hasPatient = true }));
        Assert.Contains(d.ApprovalBlockers, b => b.Contains("출연 동의"));
        var blocked = await _app.Doctor.PostAsJsonAsync($"/api/shorts/drafts/{d.Id}/approve", new { version = d.Version });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        d = await Read<DraftDto>(await _app.Staff.PutAsJsonAsync($"/api/shorts/drafts/{d.Id}", new { version = d.Version, consentConfirmed = true }));
        Assert.Empty(d.ApprovalBlockers);
    }

    [Fact]
    public async Task 반려하면_이유가_남고_원장이_다시_검토로_돌릴_수_있다()
    {
        var d = await _app.UploadAndWaitAsync();
        var doctor = _app.Doctor;

        var noReason = await doctor.PostAsJsonAsync($"/api/shorts/drafts/{d.Id}/reject", new { reason = "" });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        d = await Read<DraftDto>(await doctor.PostAsJsonAsync($"/api/shorts/drafts/{d.Id}/reject", new { reason = "훅이 너무 자극적" }));
        Assert.Equal(DraftStatus.Rejected, d.Status);
        Assert.Equal("이원장", d.RejectedByName);

        d = await Read<DraftDto>(await doctor.PostAsync($"/api/shorts/drafts/{d.Id}/reopen", null));
        Assert.Equal(DraftStatus.Review, d.Status);
    }

    [Fact]
    public async Task 게시_기록을_남기면_제목이_반복_경고_목록에_들어가_다음_초안에서_비슷하면_알려준다()
    {
        var d = await ApprovedAndReady();
        var staff = _app.Staff;

        var bad = await staff.PostAsJsonAsync($"/api/shorts/drafts/{d.Id}/publish", new { youTubeUrl = "https://evil.example/x", relatedVideoLinked = true });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        d = await Read<DraftDto>(await staff.PostAsJsonAsync($"/api/shorts/drafts/{d.Id}/publish",
            new { youTubeUrl = "https://www.youtube.com/shorts/abcDEF12345?feature=share", relatedVideoLinked = true }));
        Assert.Equal(DraftStatus.Published, d.Status);
        Assert.Equal("https://youtube.com/shorts/abcDEF12345", d.YouTubeUrl);
        Assert.True(d.RelatedVideoLinked);

        var del = await staff.DeleteAsync($"/api/shorts/drafts/{d.Id}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode); // 게시 기록은 지우지 않음

        var next = await _app.UploadAndWaitAsync();
        Assert.Equal("S1116B", next.Code);
        var similar = Assert.Single(next.Findings, f => f.Source == FindingSource.Similar);
        Assert.False(similar.Blocking); // 참고용: 승인을 막지 않음
    }

    private async Task<DraftDto> ApprovedAndReady()
    {
        _app.Stt.Words = FakeSpeechToText.Sentence(1_000, "미녹시딜은 하루 두 번 바릅니다.");
        var d = await _app.UploadAndWaitAsync();
        await Read<DraftDto>(await _app.Doctor.PostAsJsonAsync($"/api/shorts/drafts/{d.Id}/approve", new { version = d.Version }));
        return await WaitAsync(_app.Staff, d.Id);
    }

    [Fact]
    public async Task 렌더가_실패하면_이유를_보여주고_다시_시도할_수_있다()
    {
        _app.Video.FailRender = true;
        var d = await _app.UploadAndWaitAsync();
        Assert.Equal(DraftStatus.Failed, d.Status);
        Assert.Contains("가짜 실패", d.Error);
        Assert.NotEmpty(d.Cues); // 분석은 끝났음 → 다시 시도는 미리보기만

        _app.Video.FailRender = false;
        var retried = await Read<DraftDto>(await _app.Staff.PostAsync($"/api/shorts/drafts/{d.Id}/retry", null));
        Assert.Equal(DraftStatus.Rendering, retried.Status);
        Assert.Equal(DraftStatus.Review, (await WaitAsync(_app.Staff, d.Id)).Status);
    }

    [Fact]
    public async Task 소리가_없거나_너무_긴_촬영본은_이유와_함께_실패한다()
    {
        _app.Video.Probe = new ProbeResult(20_000, 1080, 1920, HasAudio: false);
        var d = await _app.UploadAndWaitAsync();
        Assert.Equal(DraftStatus.Failed, d.Status);
        Assert.Contains("소리가 없는", d.Error);

        _app.Video.Probe = new ProbeResult(6 * 60_000, 1080, 1920, true);
        d = await _app.UploadAndWaitAsync();
        Assert.Contains("5분보다 깁니다", d.Error);

        // 실패한 초안은 지울 수 있음
        Assert.Equal(HttpStatusCode.NoContent, (await _app.Staff.DeleteAsync($"/api/shorts/drafts/{d.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _app.Staff.GetAsync($"/api/shorts/drafts/{d.Id}")).StatusCode);
    }

    [Fact]
    public async Task 영상이_아닌_파일과_다른_사이트의_단순_전송은_받지_않는다()
    {
        var staff = _app.Staff;
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await UploadAsync(staff, type: "text/plain")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(staff, name: "문서.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await UploadAsync(staff, name: "clip.MOV", type: "application/octet-stream")).StatusCode);
    }

    [Fact]
    public async Task 권한_간호사는_못_쓰고_로그인_안_하면_401_화면은_로그인으로()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.ClientAs(null).GetAsync("/api/shorts/drafts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(_app.ClientAs(StaffRoles.Nurse))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _app.ClientAs(StaffRoles.Admin).GetAsync("/api/shorts/drafts")).StatusCode);

        var page = await _app.ClientAs(null).GetAsync("/shorts");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.StartsWith("/login?next=", page.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Forbidden, (await _app.ClientAs(StaffRoles.Nurse).GetAsync("/shorts/review?id=1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _app.Staff.GetAsync("/shorts")).StatusCode);
    }

    [Fact]
    public async Task 상태_화면은_빠진_설정과_승인_권한을_알려준다()
    {
        var s = await Read<SystemStatusDto>(await _app.Staff.GetAsync("/api/shorts/status"));
        Assert.Equal("ffmpeg version fake", s.Ffmpeg);
        Assert.False(s.AiEnabled);
        Assert.False(s.CanApprove);
        Assert.True((await Read<SystemStatusDto>(await _app.Doctor.GetAsync("/api/shorts/status"))).CanApprove);
    }

    [Fact]
    public async Task 기존_제목_목록을_가져오면_같은_영상은_갱신만_한다()
    {
        const string tsv = "순번\t영상ID\t제목\t조회수\n0\tfcBA_BuA4HM\t탈모약 효과 3배 보는 법\t3900\n1\tCZT27cC28NY\t집에서 흰머리 없애는 꿀팁\t10000";
        var first = await Read<TitleImportResult>(await _app.Staff.PostAsJsonAsync("/api/shorts/titles/import", new { text = tsv }));
        Assert.Equal(new TitleImportResult(2, 0, 2), first);

        var again = await Read<TitleImportResult>(await _app.Staff.PostAsJsonAsync("/api/shorts/titles/import", new { text = tsv.Replace("3900", "4100") }));
        Assert.Equal(new TitleImportResult(0, 1, 2), again);
    }
}
