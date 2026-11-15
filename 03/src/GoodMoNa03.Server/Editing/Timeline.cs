namespace GoodMoNa03.Server.Editing;

/// <summary>원본 영상 기준 시간 구간(밀리초).</summary>
public sealed record TimeRange(int StartMs, int EndMs)
{
    public int Length => EndMs - StartMs;
}

/// <summary>음성 인식 단어 하나(원본 기준 시간).</summary>
public sealed record Word(int StartMs, int EndMs, string Text);

/// <summary>무음 자르기 후보 하나. Removed=false면 검토에서 "자르지 않음"으로 되돌린 것.</summary>
public sealed record Cut(int StartMs, int EndMs, bool Removed);

/// <summary>
/// 무음 구간 → 자를 구간. 원본은 건드리지 않고 "자를 구간 목록"만 만듭니다.
/// 렌더할 때 남길 구간(<see cref="Keeps"/>)으로 바꿔 ffmpeg에 넘깁니다.
/// </summary>
public static class CutPlanner
{
    /// <summary>자를 구간이 이보다 짧으면 자르지 않습니다(튀는 느낌만 생김).</summary>
    public const int MinCutMs = 100;

    /// <summary>
    /// 무음 구간마다 앞뒤로 <paramref name="padMs"/>를 남기고 가운데를 자릅니다.
    /// 영상 맨 앞·맨 뒤 무음은 말 시작 전·끝난 뒤 여유만 남기고 모두 자릅니다.
    /// </summary>
    public static List<Cut> Plan(int durationMs, IEnumerable<TimeRange> silences, int minSilenceMs, int padMs)
    {
        var cuts = new List<Cut>();
        foreach (var s in silences.OrderBy(s => s.StartMs))
        {
            var start = Math.Clamp(s.StartMs, 0, durationMs);
            var end = Math.Clamp(s.EndMs, 0, durationMs);
            if (end - start < minSilenceMs && start > 0 && end < durationMs) continue;

            var cutStart = start <= 0 ? 0 : start + padMs;
            var cutEnd = end >= durationMs ? durationMs : end - padMs;
            if (cutEnd - cutStart < MinCutMs) continue;

            // 앞 구간과 겹치면 합칩니다(감지 결과가 겹쳐 오는 경우).
            if (cuts.Count > 0 && cutStart <= cuts[^1].EndMs)
            {
                cuts[^1] = cuts[^1] with { EndMs = Math.Max(cuts[^1].EndMs, cutEnd) };
                continue;
            }
            cuts.Add(new Cut(cutStart, cutEnd, true));
        }
        return cuts;
    }

    /// <summary>자르기로 한 구간을 뺀 나머지 = 남길 구간(시간 순).</summary>
    public static List<TimeRange> Keeps(int durationMs, IEnumerable<Cut> cuts)
    {
        var keeps = new List<TimeRange>();
        var cursor = 0;
        foreach (var c in cuts.Where(c => c.Removed).OrderBy(c => c.StartMs))
        {
            var start = Math.Clamp(c.StartMs, 0, durationMs);
            var end = Math.Clamp(c.EndMs, 0, durationMs);
            if (start > cursor) keeps.Add(new TimeRange(cursor, start));
            cursor = Math.Max(cursor, end);
        }
        if (cursor < durationMs) keeps.Add(new TimeRange(cursor, durationMs));
        return keeps;
    }

    public static int OutputDuration(IEnumerable<TimeRange> keeps) => keeps.Sum(k => k.Length);
}

/// <summary>
/// Whisper 단어 시간 바로잡기. 실제로 돌려 보면 Whisper는 단어 경계를 쉼 구간까지 늘려 잡습니다
/// (말이 1.2초에 시작해도 첫 단어가 0초부터, 문장 끝 단어가 뒤 쉼 1.6초까지 차지).
/// ffmpeg가 찾은 무음 구간은 정확하므로, 단어가 무음에 걸치면 그만큼 시작·끝을 당깁니다.
/// 그래야 자막이 무음 동안 떠 있지 않고, 쉼에서 자막 장을 끊는 규칙(CueGapMs)도 작동합니다.
/// </summary>
public static class WordAligner
{
    public static List<Word> Snap(IReadOnlyList<Word> words, IEnumerable<TimeRange> silences)
    {
        var quiet = silences.Where(s => s.Length >= 150).OrderBy(s => s.StartMs).ToList();
        var result = new List<Word>(words.Count);
        foreach (var w in words)
        {
            int start = w.StartMs, end = w.EndMs;
            foreach (var s in quiet)
            {
                if (s.EndMs <= start || s.StartMs >= end) continue;
                if (s.StartMs <= start && s.EndMs >= end)
                {
                    // 단어가 통째로 무음 안: Whisper가 시간을 앞당겨 잡은 것 → 말이 다시 시작되는 무음 끝으로 옮김
                    // (그대로 두면 무음 컷에 같이 잘려 자막에서 사라짐). 영상 끝까지 무음이면 무음 시작으로.
                    start = end = s.EndMs == int.MaxValue ? s.StartMs : s.EndMs;
                    break;
                }
                if (s.StartMs <= start) start = s.EndMs;   // 앞이 무음에 걸침
                else if (s.EndMs >= end) end = s.StartMs;  // 뒤가 무음에 걸침
                // 무음이 단어 한가운데에만 있으면 그대로(말 사이 짧은 숨)
            }
            result.Add(w with { StartMs = start, EndMs = end });
        }
        return result;
    }
}

/// <summary>원본 시간 ↔ 편집본(자른 뒤) 시간 변환. 자막은 원본 기준으로 저장하고 렌더할 때만 바꿉니다.</summary>
public static class TimeMap
{
    /// <summary>원본 시각이 남긴 구간 안이면 편집본 시각, 잘린 자리면 null.</summary>
    public static int? ToOutput(int sourceMs, IReadOnlyList<TimeRange> keeps)
    {
        var offset = 0;
        foreach (var k in keeps)
        {
            if (sourceMs < k.StartMs) return null;
            if (sourceMs <= k.EndMs) return offset + sourceMs - k.StartMs;
            offset += k.Length;
        }
        return null;
    }

    /// <summary>
    /// 원본 구간을 편집본 구간으로. 시작이 잘린 자리면 다음 남긴 구간의 시작으로, 끝이 잘린 자리면 앞 남긴 구간의 끝으로 당깁니다.
    /// 구간 전체가 잘렸으면 null.
    /// </summary>
    public static (int StartMs, int EndMs)? ToOutput(int sourceStart, int sourceEnd, IReadOnlyList<TimeRange> keeps)
    {
        int? start = null, end = null;
        var offset = 0;
        foreach (var k in keeps)
        {
            var s = Math.Max(sourceStart, k.StartMs);
            var e = Math.Min(sourceEnd, k.EndMs);
            if (s < e)
            {
                start ??= offset + s - k.StartMs;
                end = offset + e - k.StartMs;
            }
            offset += k.Length;
        }
        return start is { } a && end is { } b && b > a ? (a, b) : null;
    }
}
