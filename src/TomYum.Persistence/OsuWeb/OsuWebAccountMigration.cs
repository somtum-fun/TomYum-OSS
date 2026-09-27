using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;

namespace TomYum.Persistence.OsuWeb;

/// <summary>What happens to one TomYum account when moving to osu-web.</summary>
public enum AccountMigrationAction
{
    /// <summary>A new osu-web account is created (same name, email, password hash, country, staff).</summary>
    Create,

    /// <summary>An osu-web account with the same name and email already exists; it's the same person.</summary>
    Link,

    /// <summary>Not moved; its scores/stats are deleted (the Development seed user, or ids with no account).</summary>
    Drop,

    /// <summary>Can't be moved automatically: the name or email belongs to a different osu-web account.</summary>
    Conflict,
}

/// <summary>One row of the plan: a TomYum user id and what becomes of it.</summary>
public sealed record AccountMigrationEntry(int OldId, string Username, AccountMigrationAction Action, int? NewId, string Reason);

/// <summary>The full plan, computed without changing anything.</summary>
public sealed record AccountMigrationPlan(IReadOnlyList<AccountMigrationEntry> Entries, IReadOnlyList<string> Blockers, bool AlreadyDone)
{
    public IEnumerable<AccountMigrationEntry> Conflicts => Entries.Where(e => e.Action == AccountMigrationAction.Conflict);

    /// <summary>User ids that have scores/stats/medals but no TomYum account row.</summary>
    public IEnumerable<AccountMigrationEntry> Unmapped => Entries.Where(e => e.Action == AccountMigrationAction.Drop && e.Username.Length == 0);
}

/// <summary>What <see cref="OsuWebAccountMigration.ApplyAsync"/> did.</summary>
public sealed record AccountMigrationResult(int Created, int Linked, int Dropped, int ScoresRemapped, int ScoresDeleted);

/// <summary>
/// One-time move of an existing TomYum server's players into osu-web, before the server is
/// switched to osu-web accounts (<see cref="DatabaseOptions.OsuWebName"/>): creates (or links)
/// osu-web accounts for TomYum's users, then renumbers every user id in TomYum's scores, stats and
/// medals to the osu-web ids. After that the history can be copied with
/// <see cref="OsuWebScoreMirror.CopyHistoryAsync"/>. See docs/osu-web.md.
/// </summary>
/// <remarks>
/// Refuses to run once the server has already recorded plays in osu-web mode (TomYum's tables
/// would then mix old and new ids), and records its completion so a re-run skips straight to the
/// history copy. TomYum's own <c>users</c> table is left as it was, as a record of the old ids.
/// </remarks>
public sealed class OsuWebAccountMigration
{
    /// <summary>Email of the fixed-id Development seed user (Program.cs), which is never moved.</summary>
    public const string DevSeedEmail = "tomyum@tomyum.local";

    private const string StateTableDdl =
        """
        CREATE TABLE IF NOT EXISTS `osu_web_migration` (
          `Step` varchar(32) NOT NULL,
          `CompletedAt` datetime(6) NOT NULL,
          PRIMARY KEY (`Step`)
        ) CHARACTER SET=utf8mb4;
        """;

    private const string AccountsStep = "accounts";

    // TomYum tables keyed by user id (column UserId).
    private static readonly string[] UserIdTables = ["scores", "user_stats", "user_achievements"];

    private readonly TomYumDbContext _tomyum;
    private readonly OsuWebUserRepository _osuWebUsers;

    public OsuWebAccountMigration(TomYumDbContext tomyum, OsuWebUserRepository osuWebUsers)
    {
        _tomyum = tomyum;
        _osuWebUsers = osuWebUsers;
    }

    /// <summary>Works out what would happen, without changing anything.</summary>
    public async Task<AccountMigrationPlan> PlanAsync(CancellationToken cancellationToken = default)
    {
        await _tomyum.Database.ExecuteSqlRawAsync(StateTableDdl, cancellationToken).ConfigureAwait(false);
        if (await IsDoneAsync(cancellationToken).ConfigureAwait(false))
            return new AccountMigrationPlan([], [], AlreadyDone: true);

        var blockers = new List<string>();
        if (await CountLinkedScoresAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            blockers.Add(
                "The server has already recorded plays with osu-web accounts (osu_web_score_links has rows), so TomYum's " +
                "tables mix old and osu-web user ids. Restore a backup from before the switch and run this first.");
        }

        var entries = new List<AccountMigrationEntry>();
        var users = await _tomyum.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var user in users)
            entries.Add(await PlanUserAsync(user, cancellationToken).ConfigureAwait(false));

        var known = users.Select(u => u.Id).ToHashSet();
        foreach (var orphan in (await ReferencedUserIdsAsync(cancellationToken).ConfigureAwait(false)).Where(id => !known.Contains(id)))
            entries.Add(new AccountMigrationEntry(orphan, string.Empty, AccountMigrationAction.Drop, null, "has scores/stats but no TomYum account"));

        return new AccountMigrationPlan(entries, blockers, AlreadyDone: false);
    }

    /// <summary>
    /// Carries out <paramref name="plan"/>: creates the osu-web accounts, then renumbers TomYum's
    /// tables in one transaction and deletes rows of dropped users. Refuses while there are blockers
    /// or conflicts, and while there are unmapped ids unless <paramref name="dropUnmapped"/>.
    /// </summary>
    public async Task<AccountMigrationResult> ApplyAsync(AccountMigrationPlan plan, bool dropUnmapped, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.AlreadyDone)
            throw new InvalidOperationException("Accounts were already moved to osu-web.");
        if (plan.Blockers.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, plan.Blockers));
        if (plan.Conflicts.Any())
            throw new InvalidOperationException("Resolve the conflicts first (rename the account on one side): " +
                string.Join(", ", plan.Conflicts.Select(c => $"{c.Username} (#{c.OldId})")));
        if (!dropUnmapped && plan.Unmapped.Any())
            throw new InvalidOperationException("Some user ids have scores/stats but no account: " +
                string.Join(", ", plan.Unmapped.Select(u => u.OldId)) + ". Re-run with --drop-unmapped to delete them.");

        // 1. osu-web accounts. Not in the TomYum transaction (other database); a re-run after a
        //    failure sees them as same-name-and-email accounts and links them.
        var mapping = new Dictionary<int, int>();
        var created = 0;
        foreach (var entry in plan.Entries)
        {
            switch (entry.Action)
            {
                case AccountMigrationAction.Link:
                    mapping[entry.OldId] = entry.NewId!.Value;
                    break;
                case AccountMigrationAction.Create:
                    var user = await _tomyum.Users.AsNoTracking()
                        .FirstAsync(u => u.Id == entry.OldId, cancellationToken).ConfigureAwait(false);
                    var account = await _osuWebUsers.AddAsync(CopyWithoutId(user), cancellationToken).ConfigureAwait(false);
                    mapping[entry.OldId] = account.Id;
                    created++;
                    break;
            }
        }

        // 2. Renumber TomYum's tables. Two passes (to -new, then flip the sign) so no row ever
        //    collides with another row's old id on a unique key such as user_stats (UserId, Mode).
        var dropped = plan.Entries.Where(e => e.Action == AccountMigrationAction.Drop).Select(e => e.OldId).ToList();
        int scoresRemapped = 0, scoresDeleted = 0;
        var transaction = await _tomyum.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            // Table names come from the fixed UserIdTables list, never from input; values are parameters.
            foreach (var table in UserIdTables)
            {
                var deleteSql = $"DELETE FROM `{table}` WHERE UserId = {{0}}";
                foreach (var oldId in dropped)
                {
                    var deleted = await _tomyum.Database.ExecuteSqlRawAsync(deleteSql, [oldId], cancellationToken).ConfigureAwait(false);
                    if (table == "scores")
                        scoresDeleted += deleted;
                }

                var moveSql = $"UPDATE `{table}` SET UserId = {{0}} WHERE UserId = {{1}}";
                foreach (var (oldId, newId) in mapping)
                {
                    var moved = await _tomyum.Database.ExecuteSqlRawAsync(moveSql, [-newId, oldId], cancellationToken).ConfigureAwait(false);
                    if (table == "scores")
                        scoresRemapped += moved;
                }

                var flipSql = $"UPDATE `{table}` SET UserId = -UserId WHERE UserId < 0";
                await _tomyum.Database.ExecuteSqlRawAsync(flipSql, cancellationToken).ConfigureAwait(false);
            }

            // Web sessions (session plugin) are tied to old ids; everyone simply logs in again.
            if (await TableExistsAsync("sessions", cancellationToken).ConfigureAwait(false))
                await _tomyum.Database.ExecuteSqlRawAsync("DELETE FROM `sessions`", cancellationToken).ConfigureAwait(false);

            await _tomyum.Database
                .ExecuteSqlRawAsync("INSERT INTO `osu_web_migration` (Step, CompletedAt) VALUES ({0}, UTC_TIMESTAMP(6))", [AccountsStep], cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new AccountMigrationResult(
            created,
            plan.Entries.Count(e => e.Action == AccountMigrationAction.Link),
            dropped.Count,
            scoresRemapped,
            scoresDeleted);
    }

    private async Task<AccountMigrationEntry> PlanUserAsync(User user, CancellationToken cancellationToken)
    {
        if (string.Equals(user.Email, DevSeedEmail, StringComparison.OrdinalIgnoreCase))
            return new(user.Id, user.Username, AccountMigrationAction.Drop, null, "Development seed user (known password); not moved");

        var sameName = await _osuWebUsers.GetBySafeNameAsync(user.SafeName, cancellationToken).ConfigureAwait(false);
        if (sameName is not null)
        {
            return string.Equals(sameName.Email, user.Email, StringComparison.OrdinalIgnoreCase)
                ? new(user.Id, user.Username, AccountMigrationAction.Link, sameName.Id, "same name and email already on osu-web")
                : new(user.Id, user.Username, AccountMigrationAction.Conflict, sameName.Id, $"name taken on osu-web by #{sameName.Id} with a different email");
        }

        if (!string.IsNullOrEmpty(user.Email) && await _osuWebUsers.EmailExistsAsync(user.Email, cancellationToken).ConfigureAwait(false))
            return new(user.Id, user.Username, AccountMigrationAction.Conflict, null, "email already used by a differently named osu-web account");

        return new(user.Id, user.Username, AccountMigrationAction.Create, null, "new osu-web account");
    }

    private async Task<bool> IsDoneAsync(CancellationToken cancellationToken) =>
        (await _tomyum.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM osu_web_migration WHERE Step = {AccountsStep}")
            .ToListAsync(cancellationToken).ConfigureAwait(false))[0] > 0;

    private async Task<long> CountLinkedScoresAsync(CancellationToken cancellationToken) =>
        await TableExistsAsync("osu_web_score_links", cancellationToken).ConfigureAwait(false)
            ? (await _tomyum.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM osu_web_score_links")
                .ToListAsync(cancellationToken).ConfigureAwait(false))[0]
            : 0;

    private async Task<bool> TableExistsAsync(string table, CancellationToken cancellationToken) =>
        (await _tomyum.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = {table}")
            .ToListAsync(cancellationToken).ConfigureAwait(false))[0] > 0;

    private async Task<IReadOnlyList<int>> ReferencedUserIdsAsync(CancellationToken cancellationToken)
    {
        var ids = new SortedSet<int>();
        ids.UnionWith(await _tomyum.Scores.Select(s => s.UserId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false));
        ids.UnionWith(await _tomyum.UserStats.Select(s => s.UserId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false));
        ids.UnionWith(await _tomyum.UserAchievements.Select(a => a.UserId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false));
        return ids.ToList();
    }

    private static User CopyWithoutId(User user) => new()
    {
        Username = user.Username,
        SafeName = user.SafeName,
        Email = user.Email,
        PasswordBcrypt = user.PasswordBcrypt,
        Country = user.Country,
        CreationTime = user.CreationTime,
        LatestActivity = user.LatestActivity,
        Privileges = user.Privileges,
    };
}
