using Microsoft.EntityFrameworkCore;
using SerialLogHub.Api.Entities;

namespace SerialLogHub.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<UserPortEntity> UsersPort => Set<UserPortEntity>();
    public DbSet<CommandEntity> Commands => Set<CommandEntity>();
    public DbSet<CommandHistoryEntity> CommandHistories => Set<CommandHistoryEntity>();
    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<TelemetryRecordEntity> TelemetryRecords => Set<TelemetryRecordEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<UserEntity>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Email).IsUnique();
        });

        modelBuilder.Entity<UserPortEntity>(entity =>
        {
            entity.ToTable("UsersPort");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.UserEmail).IsUnique();
        });

        modelBuilder.Entity<CommandEntity>(entity =>
        {
            entity.ToTable("Commands");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Email, e.CommandID });
            entity.HasIndex(e => new { e.Email, e.Status, e.IsProcessed });
        });

        modelBuilder.Entity<CommandHistoryEntity>(entity =>
        {
            entity.ToTable("CommandHistories");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Email, e.CommandID });
        });

        modelBuilder.Entity<ApiKeyEntity>(entity =>
        {
            entity.ToTable("ApiKeys");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Key).IsUnique();
        });

        modelBuilder.Entity<DeviceEntity>(entity =>
        {
            entity.ToTable("Devices");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserEmail, e.PortName }).IsUnique();
        });

        modelBuilder.Entity<TelemetryRecordEntity>(entity =>
        {
            entity.ToTable("TelemetryRecords");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.UserEmail, e.RecordedAt });
            entity.HasIndex(e => new { e.DeviceId, e.RecordedAt }).IsUnique();
            entity.HasOne(e => e.Device)
                .WithMany(d => d.TelemetryRecords)
                .HasForeignKey(e => e.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
