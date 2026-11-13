using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GoodMoNa02.Server.Data;

/// <summary>
/// 인트라넷 DB: 직원 계정·로그인 세션. 앞으로 공지·연차·출퇴근·품의·주차·메신저·위키가 여기에 붙습니다.
/// 환자 데이터는 두지 않습니다(01 진료 서버의 DB가 주인).
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<StaffSession> StaffSessions => Set<StaffSession>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite는 시간대를 저장하지 않으므로 모든 DateTime을 UTC로 저장·복원합니다. (01과 같은 규칙)
        builder.Properties<DateTime>().HaveConversion<UtcConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Staff>(e =>
        {
            e.Property(s => s.LoginId).HasMaxLength(30);
            e.HasIndex(s => s.LoginId).IsUnique();
            e.Property(s => s.Name).HasMaxLength(30);
            e.Property(s => s.PasswordHash).HasMaxLength(200);
            e.Property(s => s.Role).HasConversion<string>().HasMaxLength(16);
            e.Property(s => s.RequestedRole).HasConversion<string>().HasMaxLength(16);
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(s => s.Status);
            e.Property(s => s.Department).HasMaxLength(30);
            e.Property(s => s.Phone).HasMaxLength(20);
            e.Property(s => s.RejectReason).HasMaxLength(200);
        });

        b.Entity<StaffSession>(e =>
        {
            e.Property(s => s.TokenHash).HasMaxLength(64);
            e.HasIndex(s => s.TokenHash).IsUnique();
            e.HasOne(s => s.Staff).WithMany(s => s.Sessions).HasForeignKey(s => s.StaffId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
