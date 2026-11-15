using GoodMoNa03.Server.Editing;
using GoodMoNa03.Server.Media;

namespace GoodMoNa03.Server.Tests;

/// <summary>무음 자르기 · 시간 변환 · 자막 나누기 · ASS · ffmpeg 인자 (순수 함수)</summary>
public sealed class EditingTests
{
    [Fact]
    public void 앞뒤_무음은_여유만_남기고_가운데_긴_무음은_양쪽_여유를_두고_자른다()
    {
        var cuts = CutPlanner.Plan(20_000,
            [new(0, 800), new(3_000, 3_300), new(6_000, 7_000), new(19_500, int.MaxValue)],
            minSilenceMs: 400, padMs: 150);

        Assert.Equal(
            [new Cut(0, 650, true), new Cut(6_150, 6_850, true), new Cut(19_650, 20_000, true)],
            cuts); // 0.3초 무음(3.0~3.3초)은 기준(0.4초)보다 짧아 그대로 둠
    }

    [Fact]
    public void 남길_구간은_자를_구간의_나머지이고_되돌린_구간은_다시_남긴다()
    {
        List<Cut> cuts = [new(0, 650, true), new(6_150, 6_850, false), new(19_650, 20_000, true)];
        var keeps = CutPlanner.Keeps(20_000, cuts);
        Assert.Equal([new TimeRange(650, 19_650)], keeps);

        cuts[1] = cuts[1] with { Removed = true };
        keeps = CutPlanner.Keeps(20_000, cuts);
        Assert.Equal([new TimeRange(650, 6_150), new TimeRange(6_850, 19_650)], keeps);
        Assert.Equal(5_500 + 12_800, CutPlanner.OutputDuration(keeps));
    }

    [Fact]
    public void Whisper가_쉼까지_늘려_잡은_단어_경계를_무음_구간으로_당긴다()
    {
        // 실제 받아쓰기에서 본 모양: 첫 단어가 앞 무음(0~1.2초)부터, 문장 끝 단어가 뒤 쉼(8.6~10.1초)까지
        List<Word> words = [new(0, 1_180, "미녹시딜"), new(1_180, 1_740, "바르기"), new(8_280, 10_000, "바르세요."), new(10_000, 10_740, "둘째,")];
        List<TimeRange> silences = [new(0, 1_190), new(8_600, 10_100), new(3_000, 3_050)];

        var snapped = WordAligner.Snap(words, silences);

        Assert.Equal(new Word(1_190, 1_740, "바르기"), snapped[1]);
        Assert.Equal(new Word(8_280, 8_600, "바르세요."), snapped[2]); // 뒤 쉼을 뺌
        Assert.Equal(new Word(10_100, 10_740, "둘째,"), snapped[3]);    // 앞 쉼을 뺌
        Assert.Equal(new Word(1_190, 1_190, "미녹시딜"), snapped[0]);   // 통째로 무음 안 → 말이 시작되는 무음 끝으로(안 그러면 컷에 잘려 사라짐)

        // 고친 뒤에는 쉼(1.5초)이 드러나 자막 장이 거기서 나뉨
        var cues = SubtitleBuilder.Build([new(1_190, 1_740, "바르기"), new(8_280, 8_600, "바르세요"), new(10_100, 10_740, "둘째")], 12, 2, 600);
        Assert.Equal(["바르기", "바르세요", "둘째"], cues.Select(c => c.Text));
    }

    [Fact]
    public void 원본_시간을_편집본_시간으로_옮기고_잘린_자리는_가까운_남은_구간으로_당긴다()
    {
        List<TimeRange> keeps = [new(1_000, 3_000), new(5_000, 8_000)];

        Assert.Equal(500, TimeMap.ToOutput(1_500, keeps));
        Assert.Null(TimeMap.ToOutput(4_000, keeps));
        Assert.Equal(3_000, TimeMap.ToOutput(6_000, keeps));

        // 자막이 잘린 자리에 걸쳐 있으면: 시작은 남은 곳부터, 끝은 남은 곳까지
        Assert.Equal((1_500, 2_500), TimeMap.ToOutput(2_500, 5_500, keeps));
        Assert.Equal((2_000, 3_000), TimeMap.ToOutput(4_000, 6_000, keeps));
        Assert.Null(TimeMap.ToOutput(3_200, 4_800, keeps)); // 통째로 잘림
    }

    [Fact]
    public void 자막은_한_줄_12자_두_줄까지_나누고_문장이_끝나면_새_장으로()
    {
        var words = FakeSpeechToText.Sentence(0,
            "미녹시딜 바르기 전에 꼭 알아야 할 것 세 가지입니다.", "첫째 하루 두 번.");
        var cues = SubtitleBuilder.Build(words, maxLineChars: 12, maxLines: 2, cueGapMs: 600);

        Assert.All(cues, c => Assert.All(c.Text.Split('\n'), line => Assert.True(line.Length <= 12, line)));
        Assert.All(cues, c => Assert.True(c.Text.Split('\n').Length <= 2));
        Assert.Equal("미녹시딜 바르기 전에\n꼭 알아야 할 것 세", cues[0].Text);
        Assert.Equal("가지입니다", cues[1].Text); // 두 줄이 차면 다음 장, 마침표 없앰
        Assert.Equal("첫째 하루 두 번", cues[2].Text); // 문장 끝에서 새 장
        Assert.Equal(cues[1].StartMs, cues[0].EndMs); // 0.3초 미만 틈은 이어 붙여 깜빡임 방지
    }

    [Fact]
    public void Whisper가_지어낸_문구는_자막에서_뺀다()
    {
        var words = new List<Word> { new(0, 500, "시청해"), new(500, 900, "주셔서"), new(900, 1_300, "감사합니다.") };
        Assert.Empty(SubtitleBuilder.Build(words, 12, 2, 600));
    }

    [Fact]
    public void 고친_자막은_사람이_넣은_줄바꿈은_두고_긴_줄만_다시_나눈다()
    {
        Assert.Equal("정수리가 비치면 약\n단계가\n아닙니다", SubtitleBuilder.Rewrap("정수리가 비치면 약 단계가\n아닙니다", 12));
    }

    [Fact]
    public void 용어_사전은_줄이_갈려도_고친다()
    {
        var terms = new Dictionary<string, string> { ["미녹 시딜"] = "미녹시딜", ["피나스 테리드"] = "피나스테리드" };
        Assert.Equal("미녹시딜과 피나스테리드", TermFixer.Apply("미녹 시딜과 피나스\n테리드", terms));
    }

    [Fact]
    public void ASS_자막은_훅_끝화면_영상코드_강조색을_담고_특수문자를_바꾼다()
    {
        var ass = AssWriter.Write(
            [new(0, 1_500, "미녹시딜 3가지\n{주의}")], "바르기 전 필수", "전체 설명은 아래 관련 영상에서", "S1116A",
            durationMs: 10_000, endCardSeconds: 2.5, fontName: "Malgun Gothic", emphasis: []);

        Assert.Contains("PlayResX: 1080", ass);
        Assert.Contains("Dialogue: 1,0:00:00.00,0:00:07.50,Hook,,0,0,0,,바르기 전 필수", ass);
        Assert.Contains("Dialogue: 2,0:00:07.50,0:00:10.00,End,,0,0,0,,전체 설명은 아래 관련 영상에서\\N상담 시 영상 코드 S1116A", ass);
        Assert.Contains("{\\c&HFFFFFF&}미녹시딜{\\r} {\\c&HFFFFFF&}3가지{\\r}\\N（주의）", ass);
        Assert.Equal("1:02:03.45", AssWriter.Time(3_723_456));
    }

    [Fact]
    public void 무음_감지_출력을_읽고_끝나지_않은_무음은_영상_끝까지로_본다()
    {
        const string stderr = """
            [silencedetect @ 000001] silence_start: -0.0213
            [silencedetect @ 000001] silence_end: 0.81 | silence_duration: 0.83
            [silencedetect @ 000001] silence_start: 6.002
            [silencedetect @ 000001] silence_end: 7.1 | silence_duration: 1.09
            [silencedetect @ 000001] silence_start: 19.4
            """;
        var s = FfmpegArgs.ParseSilence(stderr);
        Assert.Equal([new TimeRange(0, 810), new TimeRange(6_002, 7_100), new TimeRange(19_400, int.MaxValue)], s);
    }

    [Fact]
    public void 폰_세로_영상은_회전_정보를_반영해_세로로_읽는다()
    {
        const string json = """
            {"streams":[{"codec_type":"video","width":1920,"height":1080,"side_data_list":[{"side_data_type":"Display Matrix","rotation":-90}]},
                        {"codec_type":"audio"}],
             "format":{"duration":"31.533000"}}
            """;
        var p = FfmpegArgs.ParseProbe(json);
        Assert.Equal(new ProbeResult(31_533, 1080, 1920, true), p);
        Assert.True(p.IsVertical);

        var old = FfmpegArgs.ParseProbe("""{"streams":[{"codec_type":"video","width":1280,"height":720,"tags":{"rotate":"90"}}],"format":{"duration":"5"}}""");
        Assert.Equal(new ProbeResult(5_000, 720, 1280, false), old);
    }

    [Fact]
    public void 렌더_인자는_남길_구간만_이어붙이고_세로로_채우고_자막을_입힌다()
    {
        var req = new RenderRequest("C:\\src\\source.mp4", "out.mp4", "C:\\src", "sub.ass", [new(650, 6_150), new(6_850, 19_650)], Preview: true);
        var args = FfmpegArgs.Render(req, "h264_nvenc", "C:\\GoodMoNa\\fonts");
        var graph = args[args.IndexOf("-filter_complex") + 1];

        Assert.Contains("select='between(t,0.650,6.150)+between(t,6.850,19.650)'", graph);
        Assert.Contains("aselect='between(t,0.650,6.150)+between(t,6.850,19.650)'", graph);
        Assert.Contains("scale=540:960:force_original_aspect_ratio=increase,crop=540:960", graph);
        Assert.Contains("subtitles=filename=sub.ass:fontsdir='C\\:/GoodMoNa/fonts'", graph);
        Assert.Contains("h264_nvenc", args);

        var final = FfmpegArgs.Render(req with { Preview = false }, "libx264", "");
        Assert.Contains("scale=1080:1920", final[final.IndexOf("-filter_complex") + 1]);
        Assert.DoesNotContain("fontsdir", final[final.IndexOf("-filter_complex") + 1]);
        Assert.Contains("libx264", final);
    }
}
