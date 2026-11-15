using GoodMoNa02.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace GoodMoNa02.Server.Knowledge;

/// <summary>검색 결과 한 건 (점수 순)</summary>
public sealed record KbHit(int Id, KbKind Kind, string Title, string Category, string Body, DateOnly? ReviewBy, double Score, double Coverage, string Snippet);

/// <summary>
/// 승인된 자료의 검색 색인 (서버 메모리). 병원 한 곳의 자료(수백~수천 건)는 메모리로 충분하고, 외부 검색 엔진이 필요 없습니다.
/// 점수는 BM25(널리 쓰는 키워드 검색 점수)이고, 제목·검색 보조어는 본문보다 두 배로 칩니다.
/// 자료가 바뀌면 Invalidate()로 버리고, 다음 검색 때 DB에서 다시 만듭니다.
/// </summary>
public sealed class KbIndex
{
    private const double K1 = 1.2, B = 0.75;

    /// <summary>질문 조각 중 이 비율 이상이 들어 있어야 "찾음"으로 봅니다.</summary>
    public const double FoundCoverage = 0.4;

    private sealed record Entry(KbItem Item, Dictionary<string, int> Tf, int Length);

    private sealed record Snapshot(List<Entry> Entries, Dictionary<string, int> Df, double AvgLength);

    private volatile Snapshot? _snapshot;
    private readonly SemaphoreSlim _build = new(1, 1);
    private int _version;

    /// <summary>자료가 바뀌었음. 만드는 도중에 바뀌면 그 결과는 버립니다(버전 비교).</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _version);
        _snapshot = null;
    }

    public async Task<List<KbHit>> SearchAsync(AppDbContext db, string question, int take, CancellationToken ct)
    {
        var snap = await SnapshotAsync(db, ct);
        var query = KbText.QueryTokens(question);
        if (query.Count == 0 || snap.Entries.Count == 0) return [];

        var n = snap.Entries.Count;
        var hits = new List<KbHit>();
        foreach (var e in snap.Entries)
        {
            double score = 0;
            var matched = 0;
            foreach (var t in query)
            {
                if (!e.Tf.TryGetValue(t, out var tf)) continue;
                matched++;
                var df = snap.Df[t];
                var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * e.Length / snap.AvgLength));
            }
            if (matched == 0) continue;
            var i = e.Item;
            hits.Add(new KbHit(i.Id, i.Kind, i.Title, i.Category, i.Body, i.ReviewBy, score, (double)matched / query.Count, Snippet(i.Body, query)));
        }
        // 일치 비율이 같은 구간이면 점수로 (비율을 먼저 봐야 "주차"만 많이 나온 긴 문서가 정확한 FAQ를 이기지 않음)
        return hits.OrderByDescending(h => Math.Round(h.Coverage, 1)).ThenByDescending(h => h.Score).Take(take).ToList();
    }

    private async Task<Snapshot> SnapshotAsync(AppDbContext db, CancellationToken ct)
    {
        if (_snapshot is { } s) return s;
        await _build.WaitAsync(ct);
        try
        {
            if (_snapshot is { } again) return again;
            var version = Volatile.Read(ref _version);
            var items = await db.KbItems.AsNoTracking().Where(i => i.Status == KbStatus.Published).ToListAsync(ct);
            var entries = new List<Entry>(items.Count);
            var df = new Dictionary<string, int>();
            foreach (var item in items)
            {
                var tokens = new List<string>();
                var head = KbText.Tokens($"{item.Title} {item.Keywords}");
                tokens.AddRange(head);
                tokens.AddRange(head); // 제목·보조어는 두 배
                tokens.AddRange(KbText.Tokens($"{item.Category} {item.Body}"));
                var tf = tokens.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
                foreach (var t in tf.Keys) df[t] = df.GetValueOrDefault(t) + 1;
                entries.Add(new Entry(item, tf, tokens.Count));
            }
            var avg = entries.Count == 0 ? 1 : Math.Max(1, entries.Average(e => e.Length));
            var built = new Snapshot(entries, df, avg);
            if (Volatile.Read(ref _version) == version) _snapshot = built;
            return built;
        }
        finally
        {
            _build.Release();
        }
    }

    /// <summary>본문에서 질문 조각이 처음 나오는 곳 앞뒤 약 140자</summary>
    public static string Snippet(string body, IReadOnlyList<string> query, int length = 140)
    {
        var flat = body.Replace('\n', ' ');
        var lower = flat.ToLowerInvariant();
        var at = query.Select(t => lower.IndexOf(t, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, at - 40);
        var end = Math.Min(flat.Length, start + length);
        return (start > 0 ? "…" : "") + flat[start..end].Trim() + (end < flat.Length ? "…" : "");
    }
}
