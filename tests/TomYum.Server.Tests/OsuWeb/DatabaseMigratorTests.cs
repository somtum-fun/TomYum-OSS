using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Persistence;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>TomYum's schema from EF Core migrations, and databases from before them (<see cref="DatabaseMigrator"/>).</summary>
public sealed class DatabaseMigratorTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task FreshDatabase_GetsEveryMigration()
    {
        await using var db = NewTomYumContext();

        var applied = await db.MigrateAsync();

        Assert.Equal(db.Database.GetMigrations(), applied);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Empty(await db.MigrateAsync()); // running again does nothing
    }

    [MySqlFact]
    public async Task DatabaseFromEnsureCreated_IsBaselined_KeepingItsData()
    {
        // As the server made databases before migrations: EnsureCreated, then later tables by hand;
        // this one predates the chat log.
        await using (var legacy = NewTomYumContext())
        {
            await legacy.Database.EnsureCreatedAsync();
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE chat_messages");
            // Tables from later migrations didn't exist then either.
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE client_hashes");
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE verified_users");
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE discord_links");
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE custom_beatmapsets");
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE custom_beatmap_slots");
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE beatmap_comments");
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE mode_rank_history");
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE lastfm_links");
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE lastfm_scrobbles");
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE pp_reworks");
            await legacy.Database.ExecuteSqlRawAsync("DROP TABLE pp_rework_scores");
            legacy.UserStats.Add(new UserStats { UserId = 2, Mode = 0, Pp = 338.5 });
            legacy.Scores.Add(new Score { UserId = 2, BeatmapMd5 = "abc", Grade = "A", Passed = true, SubmittedAt = DateTimeOffset.UtcNow });
            await legacy.SaveChangesAsync();
            // A column added by a later migration (after saving: the model still has it).
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE scores DROP COLUMN FromLazer");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE staff_log DROP COLUMN Via");
            await legacy.Database.ExecuteSqlRawAsync("ALTER TABLE scores DROP INDEX IX_scores_OsuWebScoreId, DROP COLUMN OsuWebScoreId, DROP COLUMN PpHeld, DROP COLUMN LargeTickHits, DROP COLUMN SliderEndHits");
        }

        await using var db = NewTomYumContext();
        await db.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Contains(db.Database.GetMigrations().First(), await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(338.5, (await db.UserStats.AsNoTracking().SingleAsync()).Pp);
        Assert.Equal(0, await db.ChatLog.CountAsync()); // topped up
        // Players who already played are past the first-login ban-evasion check.
        Assert.Equal([2], await db.VerifiedUsers.Select(v => v.UserId).ToListAsync());
    }
}
