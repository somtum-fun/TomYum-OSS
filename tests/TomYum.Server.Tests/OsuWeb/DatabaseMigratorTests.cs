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
            legacy.UserStats.Add(new UserStats { UserId = 2, Mode = 0, Pp = 338.5 });
            await legacy.SaveChangesAsync();
        }

        await using var db = NewTomYumContext();
        await db.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Contains(db.Database.GetMigrations().First(), await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(338.5, (await db.UserStats.AsNoTracking().SingleAsync()).Pp);
        Assert.Equal(0, await db.ChatLog.CountAsync()); // topped up
    }
}
