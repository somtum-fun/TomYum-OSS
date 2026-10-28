using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Persistence.Repositories;
using TomYum.Server.Tests.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.Admin;

/// <summary>The staff log keeps an entry whose text is longer than its columns (cut), instead of failing the save.</summary>
public sealed class StaffLogRepositoryTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task A_summary_longer_than_its_column_is_cut_not_refused()
    {
        // Like an anti-cheat plugin's restriction: the module list a client reported, far past 512 characters.
        var modules = string.Join('|', Enumerable.Repeat("foreign module: C:.ProgramData.Example.Modules.Example.dll", 30));
        var context = await NewTomYumContextAsync();
        await using (context.ConfigureAwait(false))
        {
            var entry = new StaffLogEntry
            {
                CreatedAt = DateTime.UtcNow,
                Action = "score.anticheat_restricted",
                TargetId = 1000,
                Summary = $"Restricted a player: anti-cheat findings [{modules}].",
                Result = "anti-cheat findings and then some more words",
            };

            await new StaffLogRepository(context).AddAsync(entry);

            var stored = await context.StaffLog.AsNoTracking().SingleAsync(e => e.TargetId == 1000);
            Assert.Equal(512, stored.Summary.Length);
            Assert.StartsWith("Restricted a player: anti-cheat findings [", stored.Summary, StringComparison.Ordinal);
            Assert.EndsWith("…", stored.Summary, StringComparison.Ordinal);
            Assert.Equal(32, stored.Result!.Length);
            Assert.True(entry.Id > 0);
        }
    }
}
