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
    Task<string> WriteAsync(string question, IReadOnlyList<Passage> passages, CancellationToken ct) =>
        CompleteAsync(AnswerPrompt.System, AnswerPrompt.User(question, passages), ct);

    /// <summary>지시(system)와 글(user)을 주고 답을 받습니다. 자동 정리(정정 다듬기·자료 고르기·홈페이지 변경 반영)에도 씁니다.</summary>
    Task<string> CompleteAsync(string system, string user, CancellationToken ct);
}

public sealed class NoAnswerWriter : IAnswerWriter
{
    public AssistantStatusDto Status { get; } = new(false, "Off", null);

    public Task<string> CompleteAsync(string system, string user, CancellationToken ct) =>
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

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var client = http.CreateClient(HttpClientName);
        var body = new
        {
            model = _o.Model,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
            options = new { temperature = 0.1, num_ctx = 4096 },
            keep_alive = _o.KeepAlive,
        };
        using var res = await client.PostAsJsonAsync($"{_o.OllamaUrl.TrimEnd('/')}/api/chat", body, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}

/// <summary>
/// 서버가 켜질 때 Ollama 모델을 GPU에 미리 올립니다(첫 질문이 모델 적재를 기다리지 않게).
/// 실패해도 서버는 그대로 — 첫 질문 때 다시 올리고, 그동안은 검색 결과만 나갑니다.
/// </summary>
public sealed class OllamaWarmup(IHttpClientFactory http, IOptions<AppOptions> options, ILogger<OllamaWarmup> log) : BackgroundService
{
    public const string HttpClientName = "ollama-warmup";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value.Assistant;
        // 서버 PC를 막 켰을 때는 Ollama(사용자 로그인 시 실행)가 늦게 뜰 수 있어 몇 번 다시 시도합니다.
        for (var attempt = 1; attempt <= 5 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                // prompt 없이 보내면 모델만 올리고 끝납니다.
                using var res = await http.CreateClient(HttpClientName).PostAsJsonAsync(
                    $"{o.OllamaUrl.TrimEnd('/')}/api/generate", new { model = o.Model, keep_alive = o.KeepAlive }, stoppingToken);
                res.EnsureSuccessStatusCode();
                log.LogInformation("Ollama 모델을 미리 올렸습니다: {Model}", o.Model);
                return;
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning("Ollama 모델 미리 올리기 실패({Attempt}/5): {Message}", attempt, e.Message);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ContinueWith(_ => { });
            }
        }
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
