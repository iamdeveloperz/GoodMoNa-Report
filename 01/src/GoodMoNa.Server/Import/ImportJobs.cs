using GoodMoNa.Server.Contracts;
using GoodMoNa.Server.Infrastructure;

namespace GoodMoNa.Server.Import;

/// <summary>
/// 가져오기를 백그라운드에서 한 번에 하나만 돌립니다.
/// 사진이 수천 장이면 몇 분이 걸리는데, Cloudflare는 100초 안에 응답이 없으면 연결을 끊으므로(524)
/// 요청은 바로 202로 돌려주고 화면은 /api/import/status를 주기적으로 확인합니다.
/// </summary>
public sealed class ImportJobs(IServiceScopeFactory scopes, IHostApplicationLifetime life, ClinicClock clock, ILogger<ImportJobs> log)
{
    private readonly Lock _gate = new();
    private ImportStatusDto _status = new(ImportState.Idle, 0, 0, 0, 0, null, null, null, null);
    private Task _running = Task.CompletedTask;

    public ImportStatusDto Status
    {
        get { lock (_gate) return _status; }
    }

    public bool IsRunning => Status.State == ImportState.Running;

    /// <summary>테스트에서 끝날 때까지 기다리기 위해</summary>
    public Task Running
    {
        get { lock (_gate) return _running; }
    }

    public bool TryStart(ImportPlan plan)
    {
        lock (_gate)
        {
            if (_status.State == ImportState.Running) return false;
            _status = new ImportStatusDto(ImportState.Running, 0, plan.Patients.Count, 0, plan.PhotoCount, clock.UtcNow, null, null, null);
            _running = Task.Run(() => RunAsync(plan));
            return true;
        }
    }

    private async Task RunAsync(ImportPlan plan)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var importer = scope.ServiceProvider.GetRequiredService<LegacyImporter>();
            var result = await importer.ApplyAsync(plan, (done, photos) =>
            {
                lock (_gate) _status = _status with { Done = done, PhotosDone = photos };
            }, life.ApplicationStopping);

            lock (_gate) _status = _status with { State = ImportState.Succeeded, Done = plan.Patients.Count, FinishedAt = clock.UtcNow, Result = result };
            log.LogInformation("이전 기록 가져오기 완료: {@Result}", result);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "이전 기록 가져오기 실패");
            var message = ex is OperationCanceledException
                ? "서버가 멈추면서 가져오기가 취소됐습니다. 아무것도 저장되지 않았습니다."
                : $"가져오기에 실패해 모두 되돌렸습니다: {ex.Message}";
            lock (_gate) _status = _status with { State = ImportState.Failed, FinishedAt = clock.UtcNow, Error = message };
        }
    }
}
