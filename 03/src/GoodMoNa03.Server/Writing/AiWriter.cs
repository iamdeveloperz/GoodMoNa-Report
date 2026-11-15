using System.Net.Http.Json;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using GoodMoNa03.Server.Infrastructure;
using Microsoft.Extensions.Options;

namespace GoodMoNa03.Server.Writing;

/// <summary>
/// 제목·훅 초안과 표현 점검에 쓰는 AI. 02의 CS 도우미와 같은 구조(꺼짐 / 서버 PC Ollama / Claude API).
/// 보내는 것은 자막 글자뿐이고 영상·음성은 서버 밖으로 나가지 않습니다.
/// </summary>
public interface IAiWriter
{
    /// <summary>화면 표시용: "꺼짐", "Ollama qwen3:4b-instruct", "Claude claude-opus-5-5"</summary>
    string Label { get; }

    bool Enabled { get; }

    /// <summary>지시(system)와 글(user)을 주고 JSON 답을 받습니다. 실패하면 예외.</summary>
    Task<string> CompleteJsonAsync(string system, string user, CancellationToken ct);
}

public sealed class NoAiWriter : IAiWriter
{
    public string Label => "꺼짐";
    public bool Enabled => false;
    public Task<string> CompleteJsonAsync(string system, string user, CancellationToken ct) =>
        throw new InvalidOperationException("AI가 꺼져 있습니다.");
}

/// <summary>서버 PC의 Ollama(로컬 모델). localhost에만 열어 두세요.</summary>
public sealed class OllamaAiWriter(IHttpClientFactory http, IOptions<AppOptions> options) : IAiWriter
{
    public const string HttpClientName = "ollama";

    private readonly AiOptions _o = options.Value.Ai;

    public string Label => $"Ollama {_o.Model}";
    public bool Enabled => true;

    public async Task<string> CompleteJsonAsync(string system, string user, CancellationToken ct)
    {
        var body = new
        {
            model = _o.Model,
            stream = false,
            format = "json",
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
            options = new { temperature = 0.4, num_ctx = 8192 },
            keep_alive = _o.KeepAlive,
        };
        using var res = await http.CreateClient(HttpClientName).PostAsJsonAsync($"{_o.OllamaUrl.TrimEnd('/')}/api/chat", body, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return json.GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}

/// <summary>Claude API(공식 C# SDK). 자막 글자가 Anthropic으로 전송됩니다. 키는 데이터 폴더의 appsettings.local.json에만.</summary>
public sealed class ClaudeAiWriter : IAiWriter
{
    public const string DefaultModel = "claude-opus-5-5";

    private readonly AiOptions _o;
    private readonly AnthropicClient _client;

    public ClaudeAiWriter(IOptions<AppOptions> options)
    {
        _o = options.Value.Ai;
        _client = string.IsNullOrWhiteSpace(_o.ApiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = _o.ApiKey };
    }

    public string Model => string.IsNullOrWhiteSpace(_o.Model) ? DefaultModel : _o.Model;
    public string Label => $"Claude {Model}";
    public bool Enabled => true;

    public async Task<string> CompleteJsonAsync(string system, string user, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _o.TimeoutSeconds)));

        var response = await _client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 2048,
            System = system,
            // 짧은 글쓰기라 깊게 생각할 필요가 없음
            OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
            Messages = [new BetaUserMessageParam { Content = user }],
        }, timeout.Token);

        if (response.StopReason == "refusal") throw new InvalidOperationException("AI가 답하지 않았습니다.");
        return string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
    }
}
