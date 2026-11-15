using Microsoft.EntityFrameworkCore;

namespace GoodMoNa03.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<SourceVideo> Sources => Set<SourceVideo>();
    public DbSet<ShortDraft> Drafts => Set<ShortDraft>();
    public DbSet<ComplianceFinding> Findings => Set<ComplianceFinding>();
    public DbSet<TitleHistory> Titles => Set<TitleHistory>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SourceVideo>(e =>
        {
            e.Property(x => x.OriginalName).HasMaxLength(200);
            e.Property(x => x.StoredName).HasMaxLength(100);
            e.Property(x => x.UploadedByName).HasMaxLength(30);
            e.HasOne(x => x.Draft).WithOne(x => x.Source).HasForeignKey<ShortDraft>(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ShortDraft>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.SourceId).IsUnique();
            e.HasIndex(x => x.Status);
            e.Property(x => x.Code).HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Hook).HasMaxLength(60);
            e.Property(x => x.Title).HasMaxLength(100);
            e.Property(x => x.Hashtags).HasMaxLength(300);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasMany(x => x.Findings).WithOne(x => x.Draft).HasForeignKey(x => x.DraftId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ComplianceFinding>(e =>
        {
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.Where).HasMaxLength(30);
            e.Property(x => x.Rule).HasMaxLength(30);
            e.Property(x => x.Excerpt).HasMaxLength(300);
            e.Property(x => x.Reason).HasMaxLength(300);
            e.Property(x => x.Resolution).HasMaxLength(300);
        });

        b.Entity<TitleHistory>(e =>
        {
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.VideoId).HasMaxLength(20);
            e.HasIndex(x => x.VideoId);
        });
    }
}
