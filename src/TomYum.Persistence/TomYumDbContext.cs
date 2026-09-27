using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;

namespace TomYum.Persistence;

/// <summary>
/// The EF Core unit of work over the TomYum MySQL database. Holds the aggregate sets and applies
/// the Fluent API entity configurations in <c>Configurations/</c>. Per ADR-0001/0002 the domain
/// entities stay free of EF attributes; all mapping lives here.
/// </summary>
public sealed class TomYumDbContext : DbContext
{
    /// <summary>Creates the context over the supplied options (provider + connection string).</summary>
    public TomYumDbContext(DbContextOptions<TomYumDbContext> options) : base(options)
    {
    }

    /// <summary>Registered player accounts.</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>Cached beatmap metadata (mirrors official maps + server-local custom maps).</summary>
    public DbSet<Beatmap> Beatmaps => Set<Beatmap>();

    /// <summary>Submitted scores.</summary>
    public DbSet<Score> Scores => Set<Score>();

    /// <summary>Per-user per-mode statistics.</summary>
    public DbSet<UserStats> UserStats => Set<UserStats>();

    /// <summary>Per-user unlocked achievements (medals).</summary>
    public DbSet<UserAchievement> UserAchievements => Set<UserAchievement>();

    /// <summary>Actions taken from the staff panels.</summary>
    public DbSet<StaffLogEntry> StaffLog => Set<StaffLogEntry>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Pick up every IEntityTypeConfiguration in this assembly (UserConfiguration, etc.).
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TomYumDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
