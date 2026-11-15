using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Infrastructure;
using Microsoft.Extensions.Options;

namespace GoodMoNa02.Server.Knowledge;

/// <summary>AI 답변에 넘기는 근거 자료 한 건. N은 답변 안의 [N] 번호</summary>
public sealed record Passage(int N, int ItemId, string Kind, string Title, string Body);

/// <summary>
/// 근거 자료를 읽고 답을 쓰는 AI. 바꿔 끼울 수 있게 이 인터페이스 하나로만 부릅니다.
/// 기본은 꺼짐(<see cref="NoAnswerWriter"/>): 검색 결과만 보여 줍니다. 서버 PC의 로컬 모델은 <see cref="OllamaAnswerWriter"/>.
/// </summary>
public interface IAnswerWriter
{
    AssistantStatusDto Status { get; }

    /// <summary>답변 원문. 자료에 없으면 "자료에 없음"을 돌려주도록 지시합니다. 실패하면 예외.</summary>
    Task<string> WriteAsync(string question, IReadOnlyList<Passage> passages, CancellationToken ct);
}

public sealed class NoAnswerWriter : IAnswerWriter
{
    public AssistantStatusDto Status { get; } = new(false, "Off", null);

    public Task<string> WriteAsync(string question, IReadOnlyList<Passage> passages, CancellationToken ct) =>
        throw new InvalidOperationException("AI 답변이 꺼져 있습니다.");
}

/// <summary>
/// 서버 PC에서 도는 Ollama(로컬 모델)에 묻습니다. 질문과 자료가 서버 밖으로 나가지 않습니다.
/// Ollama는 localhost에만 열어 두세요(외부에서 직접 부르지 못하게).
/// </summary>
public sealed class OllamaAnswerWriter(IHttpClientFactory http, IOptions<AppOptions> options) : IAnswerWriter
{
    public const string HttpClientName = "ollama";

    private readonly AssistantOptions _o = options.Value.Assistant;

    public AssistantStatusDto Status => new(true, "Ollama", _o.Model);

    public async Task<string> WriteAsync(string question, IReadOnlyList<Passage> passages, CancellationToken ct)
    {
        var client = http.CreateClient(HttpClientName);
        var body = new
        {
            model = _o.Model,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = AnswerPrompt.System },
                new { role = "user", content = AnswerPrompt.User(question, passages) },
            },
            options = new { temperature = 0.1, num_ctx = 4096 },
        };
        using var res = await client.PostAsJsonAsync($"{_o.OllamaUrl.TrimEnd('/')}/api/chat", body, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}

/// <summary>AI에게 주는 지시. 작은 로컬 모델도 따르기 쉽게 짧고 단정하게 씁니다.</summary>
public static class AnswerPrompt
{
    public const string NotFound = "자료에 없음";

    public const string System =
        "당신은 굿모나의원 CS 직원을 돕는 안내 도우미입니다.\n" +
        "규칙:\n" +
        "1. 아래 [자료]에 적힌 내용만으로 답합니다. 자료에 없는 내용은 추측하지 않습니다.\n" +
        "2. 자료로 답할 수 없으면 정확히 \"" + NotFound + "\"이라고만 씁니다.\n" +
        "3. 진단·처방·시술이 맞는지 같은 의학적 판단은 답하지 말고 \"원장님 확인이 필요합니다\"라고 씁니다.\n" +
        "4. 문장마다 근거 자료 번호를 [1]처럼 붙입니다.\n" +
        "5. 한국어 존댓말로, 고객에게 그대로 안내할 수 있게 3~5문장으로 씁니다.";

    public static string User(string question, IReadOnlyList<Passage> passages)
    {
        var sb = new StringBuilder("[자료]\n");
        foreach (var p in passages) sb.Append($"[{p.N}] ({p.Kind}) {p.Title}\n{p.Body}\n\n");
        sb.Append("[질문]\n").Append(question);
        return sb.ToString();
    }
}

/// <summary>
/// AI 답변 검사: 근거 번호가 하나 이상 있고, 모두 실제로 넘긴 자료 번호여야 통과합니다.
/// 통과하지 못하면 답변을 버리고 검색 결과만 보여 줍니다(지어낸 답이 직원에게 가지 않게).
/// </summary>
public static partial class AnswerCheck
{
    public const int MaxLength = 1500;

    public static (string Text, List<int> Cited)? Accept(string? raw, int passageCount)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0 || text.Contains(AnswerPrompt.NotFound)) return null;
        var cited = Citation().Matches(text).Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();
        if (cited.Count == 0 || cited.Any(n => n < 1 || n > passageCount)) return null;
        if (text.Length > MaxLength) text = text[..MaxLength].TrimEnd() + "…";
        return (text, cited.Order().ToList());
    }

    [GeneratedRegex(@"\[(\d{1,2})\]")]
    private static partial Regex Citation();
}
