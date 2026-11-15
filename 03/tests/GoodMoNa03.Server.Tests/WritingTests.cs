using GoodMoNa03.Server.Endpoints;
using GoodMoNa03.Server.Infrastructure;
using GoodMoNa03.Server.Review;
using GoodMoNa03.Server.Writing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GoodMoNa03.Server.Tests;

/// <summary>표현 점검 · 반복 제목 · 영상 코드 · 설명란 · AI 초안 다듬기</summary>
public sealed class WritingTests
{
    [Theory]
    [InlineData("탈모 신약 나왔는데 이건 노벨상감인데", "과장")]
    [InlineData("부작용 없이 539% 자란다고?", "부작용 부정")]
    [InlineData("부작용 없이 539% 자란다고?", "근거 수치")]
    [InlineData("탈모약은 농약이다", "비교·비방")]
    [InlineData("탈모 주사 치료 제발 사기 좀 그만 당하세요", "비교·비방")]
    [InlineData("현존 최고의 탈모 치료", "최상급")]
    [InlineData("미녹시딜 효과 3배 보는 법", "근거 수치")]
    [InlineData("모발이식 무조건 망하는 이유", "효과 보장")]
    [InlineData("모발이식 4,000모 8개월 후 웨딩 사진", "경험담·전후")]
    // 2026-11-16 실제 쇼츠 1편을 돌려 보고 규칙이 놓친 표현
    [InlineData("가짜 약 쓴 사람들보다 확실히 더 낫고", "효과 보장")]
    [InlineData("지금 있는 치료들만으로도 확실히 풍성해질 수 있으니까", "효과 보장")]
    [InlineData("바르는 탈모약 역사상 최대 규모야", "최상급")]
    public void 분석_보고서에서_지적한_표현은_규칙에_걸린다(string text, string rule)
    {
        var found = ComplianceChecker.Check([new CheckText("제목", text)]);
        Assert.Contains(found, f => f.Rule == rule && f.Blocking);
    }

    [Fact]
    public void 평범한_설명은_걸리지_않는다()
    {
        Assert.Empty(ComplianceChecker.Check([new CheckText("자막 1", "미녹시딜은 하루 두 번 두피에 바릅니다\n꾸준히 쓰는 것이 중요합니다")]));
    }

    [Fact]
    public void 같은_약속을_반복한_제목은_비슷하다고_본다()
    {
        Assert.True(TitleSimilarity.Score("머리카락 굵어지는 가장 빠른 조합", "가늘어진 머리카락 굵게 만드는 조합") < TitleSimilarity.Threshold);
        Assert.True(TitleSimilarity.Score("머리카락 굵어지는 가장 빠른 조합", "머리카락 굵어지는 가장 빠른 방법") >= TitleSimilarity.Threshold);
        Assert.True(TitleSimilarity.Score("머리카락 굵어지는 가장 빠른 조합", "모발이식 하루 한 분만 하는 이유") < 0.2);

        var similar = TitleSimilarity.Similar("미녹시딜 바르기 전 꼭 알아야 할 5가지",
            ["미녹시딜 바르기 전 꼭 알아야 할 5가지", "미녹시딜 절대 하면 안 되는 행동 5가지", "샴푸 고르는 법"]);
        Assert.Equal("미녹시딜 바르기 전 꼭 알아야 할 5가지", similar[0].Title);
        Assert.DoesNotContain(similar, s => s.Title == "샴푸 고르는 법");
    }

    [Fact]
    public void 영상_코드는_날짜와_그날_순번_글자()
    {
        var day = new DateOnly(2026, 11, 16);
        Assert.Equal("S1116A", ShortCode.For(day, 0));
        Assert.Equal("S1116Z", ShortCode.For(day, 25));
        Assert.Equal("S1116AA", ShortCode.For(day, 26));
    }

    [Fact]
    public void 설명란에_요약_영상코드_UTM_해시태그를_넣는다()
    {
        var text = DescriptionBuilder.Build("{summary}\n코드 {code}\n{landingUrl}\n{hashtags}", "요약입니다.", "S1116A",
            "https://landing.example.test/?ref=yt", ["#탈모", "#모발이식"]);
        Assert.Equal("요약입니다.\n코드 S1116A\nhttps://landing.example.test/?ref=yt&utm_source=youtube&utm_medium=shorts&utm_campaign=S1116A\n#탈모 #모발이식", text);
        Assert.Equal("", DescriptionBuilder.WithUtm("", "S1116A"));
    }

    private static Copywriter Writer(IAiWriter ai) =>
        new(ai, Options.Create(new AppOptions()), NullLogger<Copywriter>.Instance);

    private const string Transcript = "미녹시딜 바르기 전에 꼭 알아야 할 것 세 가지입니다. 둘째 하루 두 번 꾸준히 바르세요.";

    [Fact]
    public async Task AI_초안은_서버가_다시_거른다_개수_길이_자막에_없는_강조어_해시태그_모양()
    {
        var copy = await Writer(new FakeAiWriter()).DraftAsync(Transcript, CancellationToken.None);

        Assert.Equal(3, copy.Hooks.Count); // 4개 중 3개만
        Assert.All(copy.Titles, t => Assert.True(t.Length <= 40));
        Assert.Equal(["미녹시딜"], copy.Emphasis); // "없는단어"는 자막에 없어 뺌
        Assert.Equal(["#미녹시딜", "#탈모치료"], copy.Hashtags);
        Assert.Null(copy.AiNote);
    }

    [Fact]
    public async Task AI가_꺼져_있거나_실패하면_첫_문장으로_초안을_만들고_이유를_남긴다()
    {
        var off = await Writer(new NoAiWriter()).DraftAsync(Transcript, CancellationToken.None);
        Assert.Equal(["미녹시딜 바르기 전에 꼭 알아야 할 것 세 가지입니다"], off.Titles);
        Assert.Empty(off.Hooks);
        Assert.Contains("AI 꺼짐", off.AiNote);

        var failed = await Writer(new FakeAiWriter { Fail = true }).DraftAsync(Transcript, CancellationToken.None);
        Assert.Contains("AI 초안 실패", failed.AiNote);
        Assert.Single(failed.Titles);

        Assert.Empty(await Writer(new FakeAiWriter { Fail = true }).ReviewAsync("아무 글", CancellationToken.None));
    }

    [Theory]
    [InlineData("https://youtube.com/shorts/fcBA_BuA4HM", "fcBA_BuA4HM")]
    [InlineData("https://www.youtube.com/shorts/fcBA_BuA4HM?feature=share", "fcBA_BuA4HM")]
    [InlineData("https://youtu.be/fcBA_BuA4HM", "fcBA_BuA4HM")]
    [InlineData("https://www.youtube.com/watch?v=fcBA_BuA4HM&t=3", "fcBA_BuA4HM")]
    [InlineData("https://evil.example/shorts/fcBA_BuA4HM", null)]
    [InlineData("youtube.com/shorts/x", null)]
    public void 유튜브_주소에서_영상ID를_읽는다(string url, string? id) => Assert.Equal(id, DraftEndpoints.YouTubeVideoId(url));

    [Fact]
    public void 제목_목록은_collect_shorts_TSV와_제목만_있는_줄을_모두_읽는다()
    {
        var rows = SystemEndpoints.ParseTitles("순번\t영상ID\t제목\t조회수\n0\tfcBA_BuA4HM\t탈모약 효과 3배 보는 법\t3900\n\n그냥 제목 한 줄\n");
        Assert.Equal(new List<(string?, string, int?)> { ("fcBA_BuA4HM", "탈모약 효과 3배 보는 법", 3900), (null, "그냥 제목 한 줄", null) }, rows);
    }
}
