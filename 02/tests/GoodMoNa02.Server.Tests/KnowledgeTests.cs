using System.Net;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Knowledge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GoodMoNa02.Server.Tests;

/// <summary>CS 안내 도우미: 자료 추출 → 초안 → 승인 → 검색 → 못 찾은 질문 → FAQ (학습 고리) · AI 답변 검사</summary>
public sealed class KnowledgeTests : IDisposable
{
    private readonly TestApp _app = new();

    public KnowledgeTests() => _app.Time.SetKst(2026, 11, 16, 10);

    public void Dispose() => _app.Dispose();

    /// <summary>가상 예시 자료 (실제 병원 정보 아님)</summary>
    private const string Sample = """
        <!-- 예시 자료: 실제 굿모나 정보가 아닙니다 -->
        # 진료 안내
        ## 진료 시간
        평일 오전 9시부터 오후 6시까지 진료합니다. 점심시간은 오후 1시~2시입니다.
        키워드: 영업시간, 운영시간
        분류: 주차
        ## 주차
        건물 지하 주차장을 2시간 무료로 이용할 수 있습니다.

        # 자주 묻는 질문
        분류: 예약
        Q: 예약을 바꾸거나 취소하려면 어떻게 하나요?
        A: 방문 전날 오후 6시까지 전화로 말씀해 주세요.
        당일 취소는 다음 예약이 어려울 수 있습니다.
        키워드: 예약 변경, 노쇼
        **Q2.** 주차는 몇 시간 무료인가요?
        **A2.** 진료받으신 분은 2시간 무료입니다.
        Q: 답이 없는 질문
        """;

    /* ---------- 추출 규칙 (순수 계산) ---------- */

    [Fact]
    public void 제목은_문서로_QA는_FAQ로_분류와_키워드를_붙여_자른다()
    {
        var r = KbExtractor.Extract(Sample, "진료안내", null);

        Assert.Equal(4, r.Items.Count);
        var hours = r.Items[0];
        Assert.Equal((KbKind.Doc, "진료 안내 · 진료 시간", "일반", "영업시간, 운영시간"), (hours.Kind, hours.Title, hours.Category, hours.Keywords));
        Assert.StartsWith("평일 오전 9시", hours.Body);
        Assert.Equal(("진료 안내 · 주차", "주차"), (r.Items[1].Title, r.Items[1].Category));

        var change = r.Items[2];
        Assert.Equal((KbKind.Faq, "예약을 바꾸거나 취소하려면 어떻게 하나요?", "예약", "예약 변경, 노쇼"), (change.Kind, change.Title, change.Category, change.Keywords));
        Assert.Equal("방문 전날 오후 6시까지 전화로 말씀해 주세요.\n당일 취소는 다음 예약이 어려울 수 있습니다.", change.Body);
        Assert.Equal(("주차는 몇 시간 무료인가요?", "진료받으신 분은 2시간 무료입니다."), (r.Items[3].Title, r.Items[3].Body)); // **Q2.** 형식도

        Assert.Contains(r.Warnings, w => w.Contains("답이 없는 질문"));
        Assert.DoesNotContain(r.Items, i => i.Body.Contains("예시 자료")); // <!-- --> 메모는 버림
    }

    [Fact]
    public void 구조가_없는_글은_이름을_제목으로_한_문서_하나이고_긴_본문은_나눈다()
    {
        var plain = KbExtractor.Extract("원장님 영상 대본입니다.\n\n시술 뒤에는 안내문을 꼭 드립니다.", "원장영상-3화", "영상");
        var doc = Assert.Single(plain.Items);
        Assert.Equal(("원장영상-3화", "영상", KbKind.Doc), (doc.Title, doc.Category, doc.Kind));

        var para = new string('가', 3000);
        var split = KbExtractor.Extract($"# 긴 안내\n{para}\n\n{para}", "x", null);
        Assert.Equal(["긴 안내 (1/2)", "긴 안내 (2/2)"], split.Items.Select(i => i.Title));
        Assert.All(split.Items, i => Assert.True(i.Body.Length <= KbLimits.Body));

        Assert.Empty(KbExtractor.Extract("  \n ", "x", null).Items);
    }

    [Fact]
    public void 검색_조각은_두_글자씩이고_연락처는_가린다()
    {
        Assert.Equal(["환불", "불은", "2시", "시간"], KbText.Tokens("환불은 2시간!"));
        Assert.Equal(["환불"], KbText.QueryTokens("환불 되나요")); // 질문 끝말(되나·나요)은 뺌
        Assert.Equal("010-****-**** 김환자 예약", KbText.MaskPii("010-1234-5678 김환자 예약"));
        Assert.Equal("주민 ******-******* 확인", KbText.MaskPii("주민 900101-1234567 확인"));
        Assert.Equal("2026-11-16 오후 2시", KbText.MaskPii("2026-11-16 오후 2시")); // 날짜는 그대로
    }

    [Fact]
    public void AI_답변은_넘긴_자료_번호를_근거로_달아야_통과한다()
    {
        Assert.Equal([1, 2], AnswerCheck.Accept("전날 6시까지 전화 주세요 [2]. 무료 주차는 2시간입니다 [1][2].", 2)!.Value.Cited);
        Assert.Null(AnswerCheck.Accept("전날까지 전화 주세요.", 2)); // 근거 없음
        Assert.Null(AnswerCheck.Accept("전날까지 전화 주세요 [3].", 2)); // 없는 자료 번호
        Assert.Null(AnswerCheck.Accept("자료에 없음", 2));
        Assert.Null(AnswerCheck.Accept("  ", 2));
    }

    /* ---------- 흐름 ---------- */

    [Fact]
    public async Task 가져온_자료는_승인해야_검색되고_못_찾은_질문은_FAQ가_되어_다음엔_찾는다()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse", name: "김간호");

        var preview = await owner.Post("/api/intranet/kb/import/preview", new { text = Sample, name = "진료안내.md" }).Ok<KbImportPreviewDto>();
        Assert.Equal(4, preview.Items.Count);
        Assert.Empty(await owner.Get<List<KbItemDto>>("/api/intranet/kb/items")); // 미리 보기는 저장 안 함

        var imported = await owner.Post("/api/intranet/kb/import", new { text = Sample, name = "진료안내.md", origin = "Claude" }).Ok<KbImportResultDto>();
        Assert.Equal(("진료안내", "Claude", 4), (imported.Import.Name, imported.Import.Origin, imported.Import.ItemCount));
        Assert.All(imported.Items, i => Assert.Equal((KbStatus.Draft, KbSource.Import), (i.Status, i.Source)));

        // 승인 전: 검색되지 않음 → 관리자에게 넘어감
        var before = await nurse.Post("/api/intranet/ask", new { question = "주차는 몇시간 무료인가요" }).Ok<AskResultDto>();
        Assert.False(before.Found);
        Assert.Null(before.Answer); // AI는 기본 꺼짐

        var approved = await owner.Post("/api/intranet/kb/approve", new { ids = imported.Items.Select(i => i.Id).ToArray() }).Ok<Dictionary<string, int>>();
        Assert.Equal(4, approved["approved"]);

        var after = await nurse.Post("/api/intranet/ask", new { question = "주차는 몇시간 무료인가요" }).Ok<AskResultDto>();
        Assert.True(after.Found);
        Assert.Equal("주차는 몇 시간 무료인가요?", after.Faqs[0].Title);
        Assert.Contains(after.Docs, d => d.Title == "진료 안내 · 주차");

        // 자료에 없는 질문 → 처리할 질문 목록 → FAQ로 만들어 바로 올림 → 다음 질문은 찾음
        var refund = await nurse.Post("/api/intranet/ask", new { question = "환불 규정 알려줘" }).Ok<AskResultDto>();
        Assert.False(refund.Found);
        var open = await owner.Get<List<AskLogDto>>("/api/intranet/kb/questions");
        var q = Assert.Single(open, l => l.Id == refund.LogId);
        Assert.Equal("김간호", q.StaffName);
        Assert.Contains(open, l => l.Id == before.LogId); // 승인 전에 못 찾은 질문도 남아 있음

        var faq = await owner.Post("/api/intranet/kb/items", new
        {
            kind = "Faq", title = "환불 규정은 어떻게 되나요?", body = "시술 전에는 전액 환불됩니다(예시).", category = "결제", publish = true, fromQuestionId = refund.LogId,
        }).Ok<KbItemDto>();
        Assert.Equal((KbStatus.Published, KbSource.Question), (faq.Status, faq.Source));
        Assert.DoesNotContain(await owner.Get<List<AskLogDto>>("/api/intranet/kb/questions"), l => l.Id == refund.LogId);
        var resolved = Assert.Single(await nurse.Get<List<AskLogDto>>("/api/intranet/ask/mine"), l => l.Id == refund.LogId);
        Assert.Equal((AskFollowUp.Resolved, (int?)faq.Id), (resolved.FollowUp, resolved.ResolvedItemId));

        var again = await nurse.Post("/api/intranet/ask", new { question = "환불은 어떻게 되나요?" }).Ok<AskResultDto>();
        Assert.Equal(faq.Id, Assert.Single(again.Faqs).Id);

        // 닫기
        (await owner.Post($"/api/intranet/kb/questions/{before.LogId}/dismiss")).EnsureSuccessStatusCode();
        Assert.Empty(await owner.Get<List<AskLogDto>>("/api/intranet/kb/questions"));
        var overview = await owner.Get<KbOverviewDto>("/api/intranet/kb/overview");
        Assert.Equal((0, 5, 0, false), (overview.Drafts, overview.Published, overview.OpenQuestions, overview.Assistant.Enabled));
    }

    [Fact]
    public async Task 직원은_제안만_하고_승인_반려_내림_가져오기는_관리자만()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse");
        var (staff, _) = await _app.MemberAsync("staff1", "Staff");

        await nurse.Post("/api/intranet/kb/import", new { text = Sample }).Status(HttpStatusCode.Forbidden);
        await nurse.Post("/api/intranet/kb/items", new { kind = "Faq", title = "q", body = "a", publish = true }).Status(HttpStatusCode.Forbidden);
        await nurse.Post("/api/intranet/kb/items", new { kind = "Faq", title = "", body = "a" }).Status(HttpStatusCode.BadRequest);
        var proposal = await nurse.Post("/api/intranet/kb/items", new { kind = "Faq", title = "주차 등록은 어디서 하나요?", body = "안내 데스크에서 차량 번호를 말씀해 주세요." }).Ok<KbItemDto>();
        Assert.Equal(KbStatus.Draft, proposal.Status);

        Assert.Empty(await nurse.Get<List<KbItemDto>>("/api/intranet/kb/items")); // 승인된 것만
        Assert.Single(await nurse.Get<List<KbItemDto>>("/api/intranet/kb/items?mine=true"));
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/intranet/kb/items/{proposal.Id}")).StatusCode); // 남의 초안
        await nurse.Post($"/api/intranet/kb/items/{proposal.Id}/approve").Status(HttpStatusCode.Forbidden);

        await owner.Post($"/api/intranet/kb/items/{proposal.Id}/reject", new { reason = "" }).Status(HttpStatusCode.BadRequest);
        var rejected = await owner.Post($"/api/intranet/kb/items/{proposal.Id}/reject", new { reason = "주차 자료와 겹침" }).Ok<KbItemDto>();
        Assert.Equal((KbStatus.Rejected, "주차 자료와 겹침"), (rejected.Status, rejected.RejectReason));
        Assert.Equal("주차 자료와 겹침", Assert.Single(await nurse.Get<List<KbItemDto>>("/api/intranet/kb/items?mine=true")).RejectReason);

        // 반려한 것도 다시 승인 가능 → 검색됨 → 내리면 안 나옴 → 승인된 것은 바로 못 지움
        await owner.Post($"/api/intranet/kb/items/{proposal.Id}/approve").Ok<KbItemDto>();
        await owner.Post($"/api/intranet/kb/items/{proposal.Id}/approve").Status(HttpStatusCode.Conflict);
        Assert.True((await staff.Post("/api/intranet/ask", new { question = "주차 등록" }).Ok<AskResultDto>()).Found);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync($"/api/intranet/kb/items/{proposal.Id}")).StatusCode);
        await owner.Post($"/api/intranet/kb/items/{proposal.Id}/archive").Ok<KbItemDto>();
        Assert.False((await staff.Post("/api/intranet/ask", new { question = "주차 등록" }).Ok<AskResultDto>()).Found);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/intranet/kb/items/{proposal.Id}")).StatusCode);

        // 관리 화면도 관리자만
        Assert.Equal(HttpStatusCode.Forbidden, (await nurse.GetAsync("/intranet/knowledge")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await nurse.GetAsync("/intranet/ask")).StatusCode);
        await nurse.Post("/api/intranet/ask", new { question = "x" }).Status(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task 질문의_연락처는_가려서_남고_재검토일이_지난_자료는_표시되며_부족함은_관리자에게_간다()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse");
        var (staff, _) = await _app.MemberAsync("staff1", "Staff");
        var price = await owner.Post("/api/intranet/kb/items", new
        {
            kind = "Doc", title = "11월 이벤트 가격", body = "이벤트 가격은 데스크에서 확인합니다(예시).", reviewBy = "2026-11-01", publish = true,
        }).Ok<KbItemDto>();
        Assert.True(price.Stale);
        await owner.Put($"/api/intranet/kb/items/{price.Id}", new { title = "x", body = "y", reviewBy = "11월" }).Status(HttpStatusCode.BadRequest);

        var r = await nurse.Post("/api/intranet/ask", new { question = "고객 010-1234-5678 이벤트 가격" }).Ok<AskResultDto>();
        Assert.Equal("고객 010-****-**** 이벤트 가격", r.Question);
        Assert.True(Assert.Single(r.Docs).Stale);
        Assert.Equal(1, (await owner.Get<KbOverviewDto>("/api/intranet/kb/overview")).Stale);

        // 찾았지만 부족함 → 처리할 질문, 남의 질문엔 평가 못 함
        await staff.Post($"/api/intranet/ask/{r.LogId}/feedback", new { helpful = false }).Status(HttpStatusCode.NotFound);
        (await nurse.Post($"/api/intranet/ask/{r.LogId}/feedback", new { helpful = false })).EnsureSuccessStatusCode();
        var open = Assert.Single(await owner.Get<List<AskLogDto>>("/api/intranet/kb/questions"));
        Assert.Equal(("고객 010-****-**** 이벤트 가격", false, "11월 이벤트 가격"), (open.Question, open.Helpful, open.TopItemTitle));
        (await nurse.Post($"/api/intranet/ask/{r.LogId}/feedback", new { helpful = true })).EnsureSuccessStatusCode();
        Assert.Empty(await owner.Get<List<AskLogDto>>("/api/intranet/kb/questions"));
    }

    [Fact]
    public async Task 표현이_달라_못_찾은_질문은_기존_자료의_검색_보조어로_붙이면_다음엔_찾는다()
    {
        var owner = await _app.OwnerAsync();
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse");
        var imported = await owner.Post("/api/intranet/kb/import", new { text = Sample }).Ok<KbImportResultDto>();
        var parking = imported.Items.Single(i => i.Title == "주차는 몇 시간 무료인가요?");
        var miss = await nurse.Post("/api/intranet/ask", new { question = "자리 남아있나?" }).Ok<AskResultDto>();
        Assert.False(miss.Found);

        await owner.Post($"/api/intranet/kb/questions/{miss.LogId}/attach", new { itemId = parking.Id }).Status(HttpStatusCode.BadRequest); // 아직 초안
        await owner.Post("/api/intranet/kb/approve", new { ids = imported.Items.Select(i => i.Id).ToArray() }).Ok<Dictionary<string, int>>();
        await nurse.Post($"/api/intranet/kb/questions/{miss.LogId}/attach", new { itemId = parking.Id }).Status(HttpStatusCode.Forbidden);
        var updated = await owner.Post($"/api/intranet/kb/questions/{miss.LogId}/attach", new { itemId = parking.Id }).Ok<KbItemDto>();
        Assert.Equal("자리 남아있나", updated.Keywords);
        Assert.Empty(await owner.Get<List<AskLogDto>>("/api/intranet/kb/questions"));

        var hit = await nurse.Post("/api/intranet/ask", new { question = "자리 남아있나요" }).Ok<AskResultDto>();
        Assert.Equal(parking.Id, Assert.Single(hit.Faqs).Id);

        var longQ = await nurse.Post("/api/intranet/ask", new { question = new string('긴', 70) }).Ok<AskResultDto>();
        await owner.Post($"/api/intranet/kb/questions/{longQ.LogId}/attach", new { itemId = parking.Id }).Status(HttpStatusCode.BadRequest);
    }

    /* ---------- AI 답변 (가짜 모델로 검사 흐름 확인) ---------- */

    private sealed class FakeWriter(Func<IReadOnlyList<Passage>, string> answer) : IAnswerWriter
    {
        public List<IReadOnlyList<Passage>> Calls { get; } = [];
        public AssistantStatusDto Status => new(true, "Fake", "test");

        public Task<string> WriteAsync(string question, IReadOnlyList<Passage> passages, CancellationToken ct)
        {
            Calls.Add(passages);
            return Task.FromResult(answer(passages));
        }
    }

    private async Task<HttpClient> WithAnswersAsync(FakeWriter writer)
    {
        _app.TestServices = s => s.Replace(ServiceDescriptor.Singleton<IAnswerWriter>(writer));
        var owner = await _app.OwnerAsync();
        var imported = await owner.Post("/api/intranet/kb/import", new { text = Sample, name = "진료안내" }).Ok<KbImportResultDto>();
        await owner.Post("/api/intranet/kb/approve", new { ids = imported.Items.Select(i => i.Id).ToArray() }).Ok<Dictionary<string, int>>();
        return (await _app.MemberAsync("nurse1", "Nurse")).Browser;
    }

    [Fact]
    public async Task AI_답변은_찾은_자료만_근거로_받고_근거가_틀리면_버린다()
    {
        var reply = "방문 전날 오후 6시까지 전화로 말씀해 주세요 [1].";
        var writer = new FakeWriter(_ => reply);
        var nurse = await WithAnswersAsync(writer);

        var ok = await nurse.Post("/api/intranet/ask", new { question = "예약 변경 어떻게 해요" }).Ok<AskResultDto>();
        var passages = Assert.Single(writer.Calls);
        Assert.Equal("FAQ", passages[0].Kind); // FAQ가 먼저
        Assert.Equal(reply, ok.Answer!.Text);
        var source = Assert.Single(ok.Answer.Sources);
        Assert.Equal((1, passages[0].ItemId, "예약을 바꾸거나 취소하려면 어떻게 하나요?"), (source.N, source.ItemId, source.Title));
        Assert.True(Assert.Single(await nurse.Get<List<AskLogDto>>("/api/intranet/ask/mine")).Answered);

        reply = "아마 무료일 거예요 [9]."; // 넘기지 않은 자료 번호 → 버림
        var bad = await nurse.Post("/api/intranet/ask", new { question = "주차 무료 시간" }).Ok<AskResultDto>();
        Assert.True(bad.Found);
        Assert.Null(bad.Answer);
        Assert.NotNull(bad.Note);
        Assert.NotEmpty(bad.Faqs); // 검색 결과는 그대로

        // 자료를 못 찾으면 AI를 부르지 않음 (지어낼 여지 없음)
        var calls = writer.Calls.Count;
        Assert.False((await nurse.Post("/api/intranet/ask", new { question = "보톡스 부작용" }).Ok<AskResultDto>()).Found);
        Assert.Equal(calls, writer.Calls.Count);
    }

    [Fact]
    public async Task 설정으로_Claude를_고르면_Claude_답변기가_켜지고_못_찾은_질문엔_부르지_않는다()
    {
        _app.TestSettings["App:Assistant:Mode"] = "Claude";
        _app.TestSettings["App:Assistant:ApiKey"] = "sk-ant-test-not-real"; // 실제 호출은 하지 않음
        var (nurse, _) = await _app.MemberAsync("nurse1", "Nurse");

        var status = await nurse.Get<AssistantStatusDto>("/api/intranet/ask/status");
        Assert.Equal((true, "Claude", "claude-opus-5-5"), (status.Enabled, status.Mode, status.Model));
        var r = await nurse.Post("/api/intranet/ask", new { question = "주차 무료 시간" }).Ok<AskResultDto>(); // 승인된 자료 없음 → AI 안 부름
        Assert.False(r.Found);
        Assert.Null(r.Answer);
    }

    [Fact]
    public async Task AI가_멈춰도_검색_결과는_나온다()
    {
        var nurse = await WithAnswersAsync(new FakeWriter(_ => throw new HttpRequestException("연결 안 됨")));
        var r = await nurse.Post("/api/intranet/ask", new { question = "점심시간 언제예요" }).Ok<AskResultDto>();
        Assert.True(r.Found);
        Assert.Null(r.Answer);
        Assert.Contains("AI 답변을 지금 쓸 수 없어", r.Note);
        Assert.Equal("진료 안내 · 진료 시간", r.Docs[0].Title);
    }
}
