using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GoodMoNa02.Server.Data;

/// <summary>
/// 2번 과제 DB. 아직 엔티티가 없습니다. 엔티티를 추가한 뒤
/// <c>dotnet ef migrations add Initial --project src/GoodMoNa02.Server</c>로 첫 마이그레이션을 만드세요.
/// 서버가 시작할 때 MigrateAsync로 자동 적용됩니다.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite는 시간대를 저장하지 않으므로 모든 DateTime을 UTC로 저장·복원합니다. (01과 같은 규칙)
        builder.Properties<DateTime>().HaveConversion<UtcConverter>();
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
