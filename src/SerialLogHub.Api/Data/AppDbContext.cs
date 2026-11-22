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
    }
}
