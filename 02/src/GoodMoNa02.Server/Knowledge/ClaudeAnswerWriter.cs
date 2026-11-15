using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Infrastructure;
using Microsoft.Extensions.Options;

namespace GoodMoNa02.Server.Knowledge;

/// <summary>
/// Claude API로 답을 씁니다(공식 Anthropic C# SDK). 로컬 모델 대신 쓰는 선택지로,
/// <b>질문(전화번호·주민번호는 가린 뒤)과 찾은 자료 몇 건이 Anthropic으로 전송</b>됩니다. 환자 기록은 보내지 않습니다.
/// API 키는 데이터 폴더의 appsettings.local.json(App:Assistant:ApiKey) 또는 ANTHROPIC_API_KEY 환경 변수에만 둡니다.
/// </summary>
public sealed class ClaudeAnswerWriter : IAnswerWriter
{
    public const string DefaultModel = "claude-opus-5-5";

    private readonly AssistantOptions _o;
    private readonly AnthropicClient _client;

    public ClaudeAnswerWriter(IOptions<AppOptions> options)
    {
        _o = options.Value.Assistant;
        _client = string.IsNullOrWhiteSpace(_o.ApiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = _o.ApiKey };
    }

    public string Model => string.IsNullOrWhiteSpace(_o.Model) ? DefaultModel : _o.Model;

    public AssistantStatusDto Status => new(true, "Claude", Model);

    public async Task<string> WriteAsync(string question, IReadOnlyList<Passage> passages, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _o.TimeoutSeconds)));

        var response = await _client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 2048,
            System = AnswerPrompt.System,
            // 짧은 Q&A라 깊게 생각할 필요가 없음 (Opus 5.5 기본은 medium)
            OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
            // 안전 분류기가 거절하면 서버가 알맞은 모델로 다시 답함
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
            Messages = [new BetaUserMessageParam { Content = AnswerPrompt.User(question, passages) }],
        }, timeout.Token);

        if (response.StopReason == "refusal") throw new InvalidOperationException("AI가 이 질문에 답하지 않았습니다.");
        var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        return text;
    }
}
