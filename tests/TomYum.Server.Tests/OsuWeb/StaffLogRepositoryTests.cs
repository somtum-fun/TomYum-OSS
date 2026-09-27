using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Persistence;
using TomYum.Persistence.Repositories;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary><see cref="StaffLogRepository"/> against a real TomYum database.</summary>
public sealed class StaffLogRepositoryTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task Filters_AndReturnsNewestFirst()
    {
        await using var db = await NewTomYumContextAsync();
        var log = new StaffLogRepository(db);
        await log.AddAsync(Entry("beatmapset.import", 10, job: "a"));
        await log.AddAsync(Entry("beatmapset.import", 11, job: "a"));
        await log.AddAsync(Entry("user.restrict", 2));
        await log.AddAsync(Entry("user.silence", 2));

        var all = await log.ListAsync(new StaffLogQuery());
        Assert.Equal(["user.silence", "user.restrict", "beatmapset.import", "beatmapset.import"], all.Select(e => e.Action));

        Assert.Equal(2, (await log.ListAsync(new StaffLogQuery(Action: "beatmapset.import"))).Count);
        Assert.Equal(2, (await log.ListAsync(new StaffLogQuery(Action: "user."))).Count);
        Assert.Equal(2, (await log.ListAsync(new StaffLogQuery(TargetId: 2))).Count);
        Assert.Equal(2, (await log.ListAsync(new StaffLogQuery(JobId: "a"))).Count);
        Assert.Equal([11L, 10L], (await log.ListAsync(new StaffLogQuery(BeforeId: all[1].Id))).Select(e => e.TargetId!.Value));
        Assert.Single(await log.ListAsync(new StaffLogQuery(Limit: 1)));
    }

    [MySqlFact]
    public async Task SchemaGuardTable_WorksForAnExistingDatabase()
    {
        await using var db = await NewTomYumContextAsync();
        // A database created before the staff log existed: the startup DDL has to add a table EF can use.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE staff_log");
        await db.EnsureRuntimeTablesAsync();
        await db.EnsureRuntimeTablesAsync(); // idempotent

        var log = new StaffLogRepository(db);
        await log.AddAsync(Entry("beatmapset.import", 5, job: "job", summary: new string('x', 512)));

        var entry = Assert.Single(await log.ListAsync(new StaffLogQuery()));
        Assert.Equal(512, entry.Summary.Length);
        Assert.Equal("job", entry.JobId);
    }

    private static StaffLogEntry Entry(string action, long target, string? job = null, string summary = "summary") => new()
    {
        CreatedAt = DateTime.UtcNow,
        ActorId = 2,
        Action = action,
        TargetId = target,
        Summary = summary,
        Result = "ok",
        JobId = job,
    };
}
