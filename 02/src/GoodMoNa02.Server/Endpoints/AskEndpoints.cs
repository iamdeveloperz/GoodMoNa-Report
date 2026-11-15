using System.Security.Claims;
using GoodMoNa02.Server.Contracts;
using GoodMoNa02.Server.Data;
using GoodMoNa02.Server.Infrastructure;
using GoodMoNa02.Server.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GoodMoNa02.Server.Endpoints;

/// <summary>
/// CS 안내 도우미: 직원 질문 → ① 승인된 FAQ ② 승인된 문서 ③ (켜져 있으면) 그 자료만 근거로 쓴 AI 답변.
/// 근거를 못 찾은 질문은 관리자에게 모여 새 FAQ가 됩니다.
/// </summary>
public static class AskEndpoints
{
    public static void MapAskEndpoints(this RouteGroupBuilder intranet)
    {
        var g = intranet.MapGroup("/ask");
        g.MapPost("/", Ask);
        g.MapPost("/{id:int}/feedback", Feedback);
        g.MapGet("/mine", Mine);
        g.MapGet("/status", (IAnswerWriter writer) => Results.Ok(writer.Status));
    }

    private static async Task<IResult> Ask(
        AskRequest req, ClaimsPrincipal user, AppDbContext db, ClinicClock clock, KbIndex index, IAnswerWriter writer,
        IOptions<AppOptions> options, ILoggerFactory logs, CancellationToken ct)
    {
        var question = KbText.MaskPii((req.Question ?? "").Trim());
        if (question.Length is < 2 or > KbLimits.Question) return Problems.Validation("question", $"질문을 2~{KbLimits.Question}자로 써 주세요.");

        var hits = await index.SearchAsync(db, question, 12, ct);
        var strong = hits.Where(h => h.Coverage >= KbIndex.FoundCoverage).ToList();
        var found = strong.Count > 0;
        var today = clock.Today;
        var faqs = strong.Where(h => h.Kind == KbKind.Faq).Take(3).Select(h => ToDto(h, today)).ToList();
        var docs = strong.Where(h => h.Kind == KbKind.Doc).Take(5).Select(h => ToDto(h, today)).ToList();
        var related = found ? [] : hits.Take(3).Select(h => ToDto(h, today)).ToList();

        AskAnswerDto? answer = null;
        string? note = null;
        if (found && writer.Status.Enabled)
        {
            // 근거는 종류와 상관없이 잘 맞는 순서대로 (보여 주는 FAQ·문서 안에서)
            var shown = faqs.Concat(docs).Select(h => h.Id).ToHashSet();
            var passages = strong.Where(h => shown.Contains(h.Id)).Select(h => ToDto(h, today)).Take(options.Value.Assistant.MaxPassages)
                .Select((h, i) => new Passage(i + 1, h.Id, h.Kind == KbKind.Faq ? "FAQ" : "문서", h.Title, h.Body)).ToList();
            try
            {
                var raw = await writer.WriteAsync(question, passages, ct);
                if (AnswerCheck.Accept(raw, passages.Count) is { } ok)
                    answer = new AskAnswerDto(ok.Text, ok.Cited.Select(n => passages[n - 1]).Select(p => new AskSourceDto(p.N, p.ItemId, p.Title)).ToList());
                else note = "AI가 자료에 맞는 답을 만들지 못해 찾은 자료만 보여 드립니다.";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logs.CreateLogger("Ask").LogWarning(ex, "AI 답변 실패");
                note = "AI 답변을 지금 쓸 수 없어 찾은 자료만 보여 드립니다.";
            }
        }
        if (!found) note = "등록된 자료에서 답을 찾지 못했습니다. 이 질문은 관리자에게 전달되어 FAQ로 정리됩니다.";

        var top = strong.FirstOrDefault() ?? hits.FirstOrDefault();
        var log = new AskLog
        {
            StaffId = AuthEndpoints.StaffId(user), StaffName = user.Identity?.Name ?? "직원", Question = question, CreatedAt = clock.UtcNow,
            Found = found, TopItemId = top?.Id, TopCoverage = Math.Round(top?.Coverage ?? 0, 2), Answered = answer is not null,
            FollowUp = found ? AskFollowUp.None : AskFollowUp.Open,
        };
        db.AskLogs.Add(log);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new AskResultDto(log.Id, question, found, faqs, docs, related, answer, note));
    }

    private static AskHitDto ToDto(KbHit h, DateOnly today) =>
        new(h.Id, h.Kind, h.Title, h.Category, h.Snippet, h.Body, Math.Round(h.Coverage, 2), h.ReviewBy, KnowledgeEndpoints.IsStale(KbStatus.Published, h.ReviewBy, today));

    /// <summary>평가: "부족함"이면 관리자 확인 목록에 올라갑니다. 본인 질문만.</summary>
    private static async Task<IResult> Feedback(int id, AskFeedbackRequest req, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var me = AuthEndpoints.StaffId(user);
        var log = await db.AskLogs.FirstOrDefaultAsync(a => a.Id == id && a.StaffId == me, ct);
        if (log is null) return Problems.NotFound("질문 기록을 찾을 수 없습니다.");
        log.Helpful = req.Helpful;
        if (!req.Helpful && log.FollowUp == AskFollowUp.None) log.FollowUp = AskFollowUp.Open;
        else if (req.Helpful && log.Found && log.FollowUp == AskFollowUp.Open) log.FollowUp = AskFollowUp.None; // 마음을 바꿈
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>내 최근 질문 20개 (다시 묻기·처리 결과 확인)</summary>
    private static async Task<IResult> Mine(ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var me = AuthEndpoints.StaffId(user);
        var logs = await db.AskLogs.AsNoTracking().Where(a => a.StaffId == me).OrderByDescending(a => a.Id).Take(20).ToListAsync(ct);
        return Results.Ok(await KnowledgeEndpoints.ToDtosAsync(db, logs, ct));
    }
}
