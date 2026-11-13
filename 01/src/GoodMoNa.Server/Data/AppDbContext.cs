using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GoodMoNa.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<Photo> Photos => Set<Photo>();
    public DbSet<CaptureToken> CaptureTokens => Set<CaptureToken>();
    public DbSet<VoiceNote> VoiceNotes => Set<VoiceNote>();
    public DbSet<Transcript> Transcripts => Set<Transcript>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite는 시간대 정보를 저장하지 않으므로, 모든 DateTime을 UTC로 저장·복원합니다.
        builder.Properties<DateTime>().HaveConversion<UtcConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Patient>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(30);
            e.HasIndex(p => new { p.Name, p.BirthDate }).IsUnique();
        });

        b.Entity<Visit>(e =>
        {
            e.Property(v => v.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(v => v.Progress).HasConversion<string>().HasMaxLength(16);
            e.Property(v => v.Memo).HasMaxLength(500);
            e.Property(v => v.ReportedBy).HasMaxLength(30);
            e.HasIndex(v => new { v.PatientId, v.VisitDate }).IsUnique();
            e.HasIndex(v => new { v.VisitDate, v.Status });
        });

        b.Entity<Photo>(e =>
        {
            e.Property(p => p.StoragePath).HasMaxLength(200);
            e.Property(p => p.ContentType).HasMaxLength(50);
            e.Property(p => p.ContentHash).HasMaxLength(64);
            e.HasIndex(p => new { p.VisitId, p.ContentHash });
        });

        b.Entity<CaptureToken>(e =>
        {
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.HasIndex(t => t.TokenHash).IsUnique();
        });

        b.Entity<VoiceNote>(e =>
        {
            e.Property(n => n.StoragePath).HasMaxLength(200);
            e.Property(n => n.ContentType).HasMaxLength(50);
            e.Property(n => n.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(n => n.Source).HasConversion<string>().HasMaxLength(16);
            e.Property(n => n.Error).HasMaxLength(500);
            e.HasIndex(n => new { n.VisitId, n.Seq }).IsUnique();
            e.HasIndex(n => n.Status);
            e.HasOne(n => n.Visit).WithMany(v => v.VoiceNotes).HasForeignKey(n => n.VisitId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Transcript>(e =>
        {
            e.Property(t => t.Model).HasMaxLength(80);
            e.Property(t => t.Runtime).HasMaxLength(40);
            e.HasIndex(t => t.VoiceNoteId).IsUnique();
            // 녹음을 지우면 DB가 스크립트도 같이 지웁니다.
            e.HasOne(t => t.VoiceNote).WithOne(n => n.Transcript).HasForeignKey<Transcript>(t => t.VoiceNoteId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
