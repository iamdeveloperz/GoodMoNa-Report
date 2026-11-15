using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GoodMoNa03.Server.Editing;

/// <summary>자막 한 장(원본 기준 시간). Text의 줄바꿈은 '\n'. 검토 화면에서 글자만 고칩니다(시간은 고정).</summary>
public sealed record Cue(int Id, int StartMs, int EndMs, string Text);

public static partial class SubtitleBuilder
{
    /// <summary>
    /// 단어 → 자막 장. 한 줄 최대 <paramref name="maxLineChars"/>자, 한 장 최대 <paramref name="maxLines"/>줄.
    /// 문장이 끝나거나(. ? !) 단어 사이가 <paramref name="cueGapMs"/> 넘게 벌어지면 새 장으로 넘깁니다.
    /// </summary>
    public static List<Cue> Build(IReadOnlyList<Word> words, int maxLineChars, int maxLines, int cueGapMs)
    {
        var cues = new List<Cue>();
        var lines = new List<string>();
        var line = new StringBuilder();
        int cueStart = 0, cueEnd = 0;

        void CloseCue()
        {
            if (line.Length > 0) lines.Add(line.ToString());
            line.Clear();
            var text = string.Join('\n', lines).Trim();
            lines.Clear();
            if (text.Length == 0 || IsHallucination(text)) return;
            cues.Add(new Cue(cues.Count + 1, cueStart, cueEnd, text));
        }

        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var text = CleanWord(w.Text);
            if (text.Length == 0) continue;

            var empty = lines.Count == 0 && line.Length == 0;
            if (empty) cueStart = w.StartMs;

            if (line.Length > 0 && line.Length + 1 + text.Length > maxLineChars)
            {
                lines.Add(line.ToString());
                line.Clear();
                if (lines.Count >= maxLines)
                {
                    // 이 장이 꽉 참: 지금 단어는 다음 장 첫 단어
                    var full = string.Join('\n', lines).Trim();
                    lines.Clear();
                    if (full.Length > 0 && !IsHallucination(full)) cues.Add(new Cue(cues.Count + 1, cueStart, cueEnd, full));
                    cueStart = w.StartMs;
                }
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(text);
            cueEnd = w.EndMs;

            var next = i + 1 < words.Count ? words[i + 1] : null;
            var sentenceEnd = EndsSentence(w.Text);
            var gap = next is not null && next.StartMs - w.EndMs > cueGapMs;
            if (next is null || sentenceEnd || gap) CloseCue();
        }
        CloseCue();
        return Tighten(cues);
    }

    /// <summary>앞 장 끝과 다음 장 시작 사이가 짧으면(0.3초 미만) 이어 붙여 자막이 깜빡이지 않게 합니다.</summary>
    private static List<Cue> Tighten(List<Cue> cues)
    {
        for (var i = 0; i + 1 < cues.Count; i++)
        {
            var gap = cues[i + 1].StartMs - cues[i].EndMs;
            if (gap is > 0 and < 300) cues[i] = cues[i] with { EndMs = cues[i + 1].StartMs };
        }
        return cues;
    }

    /// <summary>자막에는 마침표·쉼표를 쓰지 않습니다(쇼츠 관례). 물음표·느낌표는 남깁니다.</summary>
    public static string CleanWord(string text) => text.Trim().TrimEnd('.', ',', '。', '、').Trim();

    private static bool EndsSentence(string text)
    {
        var t = text.TrimEnd();
        return t.EndsWith('.') || t.EndsWith('?') || t.EndsWith('!') || t.EndsWith('…');
    }

    /// <summary>Whisper가 무음·잡음에서 지어내는 대표 문구(01과 같은 목록).</summary>
    public static bool IsHallucination(string text) => HallucinationPattern().IsMatch(text.Replace('\n', ' '));

    [GeneratedRegex(@"^(시청해\s*주셔서\s*감사합니다|구독과\s*좋아요|좋아요와\s*구독|다음\s*영상에서\s*만나요|MBC\s*뉴스|KBS\s*뉴스|자막\s*제공|[\s.…,!?·]+)[.!…\s]*$", RegexOptions.IgnoreCase)]
    private static partial Regex HallucinationPattern();

    /// <summary>
    /// 자막 글자를 다시 줄 나눔합니다(검토에서 글자를 고친 뒤). 사람이 넣은 줄바꿈은 존중하고, 너무 긴 줄만 나눕니다.
    /// </summary>
    public static string Rewrap(string text, int maxLineChars)
    {
        var result = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > maxLineChars)
                {
                    result.Add(line.ToString());
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) result.Add(line.ToString());
        }
        return string.Join('\n', result);
    }
}

/// <summary>Whisper가 자주 틀리는 표기를 바로잡습니다(긴 표현부터).</summary>
public static class TermFixer
{
    public static string Apply(string text, IReadOnlyDictionary<string, string> terms)
    {
        foreach (var (wrong, right) in terms.OrderByDescending(t => t.Key.Length))
        {
            if (wrong.Length == 0) continue;
            text = text.Replace(wrong, right, StringComparison.Ordinal);
            // 줄바꿈으로 갈라진 경우도 ("미녹\n시딜")
            if (wrong.Contains(' ')) text = text.Replace(wrong.Replace(' ', '\n'), right, StringComparison.Ordinal);
        }
        return text;
    }
}

/// <summary>강조색으로 보일 단어: 숫자가 든 단어, 약 이름 등 기본 목록, AI가 고른 단어.</summary>
public static partial class Emphasis
{
    public static readonly string[] DefaultWords =
        ["미녹시딜", "피나스테리드", "두타스테리드", "프로페시아", "아보다트", "탈모약", "모발이식", "비절개", "정수리", "헤어라인", "모낭"];

    public static bool IsEmphasis(string word, IReadOnlyCollection<string> extra)
    {
        var w = word.Trim('?', '!', '"', '\'', '“', '”');
        if (w.Length == 0) return false;
        if (DigitPattern().IsMatch(w)) return true;
        return DefaultWords.Any(d => w.Contains(d, StringComparison.Ordinal))
               || extra.Any(e => e.Length >= 2 && w.Contains(e, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"\d")]
    private static partial Regex DigitPattern();
}

/// <summary>렌더용 자막 파일(ASS). 채널 스타일: 굵은 노란 글자 + 검은 테두리, 위쪽 훅 띠, 끝 화면 안내.</summary>
public static class AssWriter
{
    public const int Width = 1080;
    public const int Height = 1920;

    // ASS 색: &HAABBGGRR
    private const string Yellow = "&H0000F0FF";
    private const string White = "&H00FFFFFF";
    private const string Black = "&H00000000";
    private const string BoxBlack = "&H33000000";

    public sealed record OutputCue(int StartMs, int EndMs, string Text);

    public static string Write(
        IReadOnlyList<OutputCue> cues, string? hook, string? endCardText, string? code,
        int durationMs, double endCardSeconds, string fontName, IReadOnlyCollection<string> emphasis)
    {
        var sb = new StringBuilder();
        sb.Append("[Script Info]\n")
          .Append("ScriptType: v4.00+\n")
          .Append(CultureInfo.InvariantCulture, $"PlayResX: {Width}\nPlayResY: {Height}\n")
          // 자막은 미리 줄을 나눠 두지만, 훅·끝 화면은 길면 자동 줄바꿈(0 = 균형 있게 나눔)
          .Append("WrapStyle: 0\nScaledBorderAndShadow: yes\n\n")
          .Append("[V4+ Styles]\n")
          .Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n")
          // 자막: 아래 가운데. 쇼츠 화면 아래쪽(제목·버튼)에 가리지 않게 MarginV를 넉넉히.
          .Append(CultureInfo.InvariantCulture, $"Style: Sub,{fontName},84,{Yellow},{Yellow},{Black},{Black},-1,0,0,0,100,100,0,0,1,7,0,2,60,60,560,1\n")
          // 훅: 위 가운데 검은 띠
          .Append(CultureInfo.InvariantCulture, $"Style: Hook,{fontName},66,{White},{White},{BoxBlack},{BoxBlack},-1,0,0,0,100,100,0,0,3,20,0,8,80,80,250,1\n")
          // 끝 화면: 가운데
          .Append(CultureInfo.InvariantCulture, $"Style: End,{fontName},62,{White},{White},{BoxBlack},{BoxBlack},-1,0,0,0,100,100,0,0,3,24,0,5,80,80,0,1\n\n")
          .Append("[Events]\n")
          .Append("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");

        var endCardMs = (int)Math.Round(endCardSeconds * 1000);
        var hasEnd = !string.IsNullOrWhiteSpace(endCardText) && endCardMs > 0 && durationMs > endCardMs + 1000;
        var endStart = hasEnd ? durationMs - endCardMs : durationMs;

        if (!string.IsNullOrWhiteSpace(hook))
            sb.Append(Dialogue(1, 0, endStart, "Hook", Escape(hook)));

        foreach (var c in cues)
        {
            if (c.EndMs <= c.StartMs) continue;
            sb.Append(Dialogue(0, c.StartMs, c.EndMs, "Sub", Highlight(c.Text, emphasis)));
        }

        if (hasEnd)
        {
            var text = Escape(endCardText!) + (string.IsNullOrWhiteSpace(code) ? "" : "\\N" + Escape($"상담 시 영상 코드 {code}"));
            sb.Append(Dialogue(2, endStart, durationMs, "End", text));
        }
        return sb.ToString();
    }

    private static string Dialogue(int layer, int startMs, int endMs, string style, string text) =>
        $"Dialogue: {layer},{Time(startMs)},{Time(endMs)},{style},,0,0,0,,{text}\n";

    /// <summary>강조 단어만 흰색으로(나머지는 스타일의 노란색).</summary>
    private static string Highlight(string text, IReadOnlyCollection<string> emphasis)
    {
        var lines = text.Split('\n').Select(line =>
            string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w =>
                Emphasis.IsEmphasis(w, emphasis) ? $"{{\\c&HFFFFFF&}}{Escape(w)}{{\\r}}" : Escape(w))));
        return string.Join("\\N", lines);
    }

    /// <summary>ASS에서 특수 의미가 있는 글자(중괄호·역슬래시)를 비슷한 모양으로 바꿉니다.</summary>
    public static string Escape(string text) =>
        text.Replace("\\", "＼").Replace("{", "（").Replace("}", "）").Replace("\r", "").Replace("\n", "\\N");

    public static string Time(int ms)
    {
        ms = Math.Max(0, ms);
        var cs = ms / 10;
        return string.Create(CultureInfo.InvariantCulture, $"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}");
    }
}
