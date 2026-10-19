using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using MowerManager.Auth;
using MowerManager.Monitoring;

namespace MowerManager.Data;

public static class DbInitializer
{
    private const string BaselineMigration = "20261019182901_InitialCreate";

    public static async Task InitializeAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDb>();

        // Databases created by EnsureCreated (before migrations existed) already match the baseline.
        var history = db.GetService<IHistoryRepository>();
        if (!await history.ExistsAsync() && await TableExistsAsync(db, "Mowers"))
        {
            await db.Database.ExecuteSqlRawAsync(history.GetCreateScript());
            await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(BaselineMigration, "10.0.13")));
        }
        await db.Database.MigrateAsync();

        if (!await db.RuleSettings.AnyAsync())
        {
            var d = services.GetRequiredService<IOptions<BatteryRuleOptions>>().Value;
            db.RuleSettings.Add(new RuleSettings { Scope = d.Scope, ThresholdPercent = d.ThresholdPercent, Action = d.Action });
            await db.SaveChangesAsync();
        }

        await AuthApi.SeedInitialAdminAsync(db, services.GetRequiredService<AuthOptions>(),
            services.GetRequiredService<IPasswordHasher<User>>(), services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DbInitializer)));
    }

    private static async Task<bool> TableExistsAsync(AppDb db, string table) =>
        await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {table}").SingleAsync() > 0;
}
