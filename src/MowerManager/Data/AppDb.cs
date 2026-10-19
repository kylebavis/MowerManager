using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MowerManager.Data;

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Mower> Mowers => Set<Mower>();
    public DbSet<MowerSnapshot> Snapshots => Set<MowerSnapshot>();
    public DbSet<RuleSettings> RuleSettings => Set<RuleSettings>();
    public DbSet<EventLogEntry> Events => Set<EventLogEntry>();
    public DbSet<User> Users => Set<User>();

    public async Task<RuleSettings> GetRuleSettingsAsync(CancellationToken ct) =>
        await RuleSettings.FindAsync([1], ct) ?? throw new InvalidOperationException("Rule settings not seeded");

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MowerSnapshot>().HasIndex(s => new { s.MowerId, s.Timestamp });
        b.Entity<EventLogEntry>().HasIndex(e => e.Timestamp);
        b.Entity<RuleSettings>().Property(s => s.Action).HasConversion<string>();
        b.Entity<RuleSettings>().Property(s => s.Scope).HasConversion<string>();
        b.Entity<User>().Property(u => u.Username).UseCollation("NOCASE");
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();
    }

    // SQLite drops DateTimeKind; everything stored is UTC.
    protected override void ConfigureConventions(ModelConfigurationBuilder c) =>
        c.Properties<DateTime>().HaveConversion<UtcConverter>();

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
