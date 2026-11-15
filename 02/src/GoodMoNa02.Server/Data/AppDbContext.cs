using Microsoft.EntityFrameworkCore;
using GoodMoNa02.Server.Contracts;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GoodMoNa02.Server.Data;

/// <summary>
/// 인트라넷 DB: 직원 계정·로그인 세션 · 공지 · 연차 · 휴무일 · 출퇴근 · 주차 · 병원 설정.
/// 환자 데이터는 두지 않습니다(01 진료 서버의 DB가 주인).
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<StaffSession> StaffSessions => Set<StaffSession>();
    public DbSet<Notice> Notices => Set<Notice>();
    public DbSet<NoticeRead> NoticeReads => Set<NoticeRead>();
    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
    public DbSet<LeaveAdjustment> LeaveAdjustments => Set<LeaveAdjustment>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<ClinicSettings> Settings => Set<ClinicSettings>();
    public DbSet<AttendanceRecord> Attendance => Set<AttendanceRecord>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<ParkingSpot> ParkingSpots => Set<ParkingSpot>();
    public DbSet<ParkingAssignment> ParkingAssignments => Set<ParkingAssignment>();
    public DbSet<ParkingRequest> ParkingRequests => Set<ParkingRequest>();

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

        // 직원은 지우지 않고 사용 중지만 하므로, 업무 기록은 직원 삭제를 막습니다(Restrict).
        b.Entity<Notice>(e =>
        {
            e.Property(n => n.Title).HasMaxLength(NoticeLimits.Title);
            e.Property(n => n.Body).HasMaxLength(NoticeLimits.Body);
            e.Property(n => n.AuthorName).HasMaxLength(30);
            e.HasIndex(n => n.CreatedAt);
        });
        b.Entity<NoticeRead>(e =>
        {
            e.HasIndex(r => new { r.NoticeId, r.StaffId }).IsUnique();
            e.HasOne(r => r.Notice).WithMany(n => n.Reads).HasForeignKey(r => r.NoticeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<LeaveRequest>(e =>
        {
            e.Property(r => r.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Portion).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Reason).HasMaxLength(200);
            e.Property(r => r.DecidedByName).HasMaxLength(30);
            e.Property(r => r.DecisionNote).HasMaxLength(200);
            e.HasIndex(r => new { r.StaffId, r.StartDate });
            e.HasIndex(r => new { r.Status, r.StartDate });
            e.HasOne(r => r.Staff).WithMany().HasForeignKey(r => r.StaffId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<LeaveAdjustment>(e =>
        {
            e.Property(a => a.Days).HasConversion<double>();
            e.Property(a => a.Reason).HasMaxLength(200);
            e.Property(a => a.CreatedByName).HasMaxLength(30);
            e.HasIndex(a => a.StaffId);
            e.HasOne(a => a.Staff).WithMany().HasForeignKey(a => a.StaffId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Holiday>(e =>
        {
            e.HasKey(h => h.Date);
            e.Property(h => h.Name).HasMaxLength(40);
        });
        b.Entity<ClinicSettings>(e =>
        {
            e.Property(s => s.AttendanceNetworks).HasMaxLength(1000);
            e.Property(s => s.AttendanceSecret).HasMaxLength(64);
            e.Property(s => s.HolidaySeedYears).HasMaxLength(200);
        });

        b.Entity<AttendanceRecord>(e =>
        {
            e.HasIndex(a => new { a.StaffId, a.Date }).IsUnique();
            e.HasIndex(a => a.Date);
            e.Property(a => a.CheckInMethod).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.CheckOutMethod).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.EditedByName).HasMaxLength(30);
            e.Property(a => a.EditReason).HasMaxLength(200);
            e.HasOne(a => a.Staff).WithMany().HasForeignKey(a => a.StaffId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Vehicle>(e =>
        {
            e.Property(v => v.Plate).HasMaxLength(20);
            e.HasIndex(v => v.Plate).IsUnique();
            e.Property(v => v.Model).HasMaxLength(40);
            e.HasOne(v => v.Staff).WithMany().HasForeignKey(v => v.StaffId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<ParkingSpot>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(20);
            e.HasIndex(p => p.Name).IsUnique();
            e.Property(p => p.Note).HasMaxLength(100);
        });
        b.Entity<ParkingAssignment>(e =>
        {
            e.HasIndex(a => new { a.SpotId, a.StartDate });
            e.HasIndex(a => a.StaffId);
            e.Property(a => a.AssignedByName).HasMaxLength(30);
            e.HasOne(a => a.Spot).WithMany().HasForeignKey(a => a.SpotId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Staff).WithMany().HasForeignKey(a => a.StaffId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Vehicle).WithMany().HasForeignKey(a => a.VehicleId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<ParkingRequest>(e =>
        {
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Note).HasMaxLength(200);
            e.Property(r => r.DecidedByName).HasMaxLength(30);
            e.Property(r => r.DecisionNote).HasMaxLength(200);
            e.HasIndex(r => r.Status);
            e.HasOne(r => r.Staff).WithMany().HasForeignKey(r => r.StaffId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.Vehicle).WithMany().HasForeignKey(r => r.VehicleId).OnDelete(DeleteBehavior.SetNull);
        });
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
}
