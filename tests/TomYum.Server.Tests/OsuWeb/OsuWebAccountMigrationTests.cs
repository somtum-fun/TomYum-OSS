using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>
/// <see cref="OsuWebAccountMigration"/> + <see cref="OsuWebScoreMirror.CopyHistoryAsync"/>: moving an
/// existing TomYum server (own accounts, scores, stats, medals) into osu-web.
/// </summary>
public sealed class OsuWebAccountMigrationTests : OsuWebDatabaseTest
{
    private const int MapId = 100;

    // Real bcrypt hashes are 60 characters, which TomYum's column enforces.
    private static readonly string AliceHash = "$2a$10$" + new string('A', 53);
    private static readonly string OtherHash = "$2a$10$" + new string('o', 53);

    [MySqlFact]
    public async Task Plan_SortsEveryAccount()
    {
        await using var tomyum = await SeedServerAsync();

        var plan = await NewMigration(tomyum).PlanAsync();

        Assert.Empty(plan.Blockers);
        Assert.Equal(AccountMigrationAction.Drop, Entry(plan, 2).Action);      // dev seed
        Assert.Equal(AccountMigrationAction.Create, Entry(plan, 3).Action);
        Assert.Equal(AccountMigrationAction.Create, Entry(plan, 4).Action);
        Assert.Equal(AccountMigrationAction.Link, Entry(plan, 5).Action);      // same name + email on osu-web
        Assert.Equal(2, Entry(plan, 5).NewId);
        Assert.Equal(AccountMigrationAction.Drop, Entry(plan, 9).Action);      // data without an account
        Assert.Single(plan.Unmapped);
    }

    [MySqlFact]
    public async Task Apply_RefusesUnmappedDataUnlessAsked()
    {
        await using var tomyum = await SeedServerAsync();
        var migration = NewMigration(tomyum);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await migration.ApplyAsync(await migration.PlanAsync(), dropUnmapped: false));

        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM phpbb_users")); // untouched
    }

    [MySqlFact]
    public async Task Apply_CreatesAccountsAndRenumbersTomYum()
    {
        await using var tomyum = await SeedServerAsync();
        var migration = NewMigration(tomyum);

        var result = await migration.ApplyAsync(await migration.PlanAsync(), dropUnmapped: true);

        Assert.Equal(2, result.Created);
        Assert.Equal(1, result.Linked);
        var alice = await IdOfAsync("alice");
        var bob = await IdOfAsync("bob smith");
        Assert.Equal(3, Math.Min(alice, bob)); // osu-web ids after the existing #2
        Assert.Equal(4, Math.Max(alice, bob));

        // Same password hash and country; Owner became osu-web staff.
        Assert.Equal(AliceHash,
            await ScalarAsync<string>($"SELECT user_password FROM phpbb_users WHERE user_id = {alice}"));
        Assert.Equal("JP", await ScalarAsync<string>($"SELECT country_acronym FROM phpbb_users WHERE user_id = {alice}"));
        Assert.Equal(1L, await ScalarAsync<long>(
            $"SELECT COUNT(*) FROM phpbb_user_group m JOIN phpbb_groups g USING (group_id) WHERE m.user_id = {alice} AND g.identifier = 'admin'"));

        // TomYum's tables now use osu-web ids; dev seed + orphan data gone.
        tomyum.ChangeTracker.Clear();
        var scoreOwners = await tomyum.Scores.Select(s => s.UserId).Distinct().OrderBy(id => id).ToListAsync();
        Assert.Equal(new[] { 2, alice, bob }.Order(), scoreOwners);
        Assert.Equal(700.0, (await tomyum.UserStats.SingleAsync(s => s.UserId == alice)).Pp);
        Assert.Equal(alice, (await tomyum.UserAchievements.SingleAsync()).UserId);
        Assert.Equal(4, result.ScoresRemapped);  // Alice (vanilla + relax), Bob, Carol
        Assert.Equal(2, result.ScoresDeleted);

        // Done once: a second plan says so instead of renumbering again.
        Assert.True((await migration.PlanAsync()).AlreadyDone);
    }

    [MySqlFact]
    public async Task Renumbering_SurvivesIdsThatSwapPlaces()
    {
        // Old #3 -> osu-web #4 while old #4 -> osu-web #3: a one-pass UPDATE would collide on user_stats' key.
        await using var tomyum = await NewTomYumContextAsync();
        await ExecuteAsync(
            "INSERT INTO phpbb_users (user_id, username, username_clean, user_email, user_sig, user_occ, user_interests) VALUES " +
            "(3, 'Second', 'second', 'second@example.com', '', '', ''), (4, 'First', 'first', 'first@example.com', '', '', '')");
        tomyum.Users.AddRange(User(3, "First", "first@example.com"), User(4, "Second", "second@example.com"));
        tomyum.UserStats.AddRange(new UserStats { UserId = 3, Mode = 0, Pp = 111 }, new UserStats { UserId = 4, Mode = 0, Pp = 222 });
        await tomyum.SaveChangesAsync();
        var migration = NewMigration(tomyum);

        await migration.ApplyAsync(await migration.PlanAsync(), dropUnmapped: false);

        tomyum.ChangeTracker.Clear();
        Assert.Equal(111.0, (await tomyum.UserStats.SingleAsync(s => s.UserId == 4)).Pp);
        Assert.Equal(222.0, (await tomyum.UserStats.SingleAsync(s => s.UserId == 3)).Pp);
    }

    [MySqlFact]
    public async Task Conflicts_BlockTheMigration()
    {
        await using var tomyum = await NewTomYumContextAsync();
        await ExecuteAsync(
            "INSERT INTO phpbb_users (user_id, username, username_clean, user_email, user_sig, user_occ, user_interests) VALUES " +
            "(2, 'Dave', 'dave', 'someone-else@example.com', '', '', '')");
        tomyum.Users.Add(User(3, "Dave", "dave@example.com"));
        await tomyum.SaveChangesAsync();
        var migration = NewMigration(tomyum);

        var plan = await migration.PlanAsync();

        Assert.Equal(AccountMigrationAction.Conflict, Entry(plan, 3).Action);
        await Assert.ThrowsAsync<InvalidOperationException>(() => migration.ApplyAsync(plan, dropUnmapped: true));
    }

    [MySqlFact]
    public async Task AServerAlreadyRunningOnOsuWebAccounts_IsBlocked()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var mirror = new OsuWebScoreMirror(tomyum, NewOsuWebContext());
        await mirror.EnsureLinkTableAsync();
        await tomyum.Database.ExecuteSqlRawAsync("INSERT INTO osu_web_score_links (ScoreId, OsuWebScoreId) VALUES (1, 1)");

        var plan = await NewMigration(tomyum).PlanAsync();

        Assert.NotEmpty(plan.Blockers);
    }

    [MySqlFact]
    public async Task CopyHistory_PutsTheOldScoresAndStatsOnOsuWeb()
    {
        await using var tomyum = await SeedServerAsync();
        var migration = NewMigration(tomyum);
        await migration.ApplyAsync(await migration.PlanAsync(), dropUnmapped: true);
        await new OsuWebBeatmapMirror(NewOsuWebContext()).BackfillAsync(await tomyum.Beatmaps.AsNoTracking().ToListAsync());
        var mirror = new OsuWebScoreMirror(tomyum, NewOsuWebContext());

        var copied = await mirror.CopyHistoryAsync();
        var again = await new OsuWebScoreMirror(tomyum, NewOsuWebContext()).CopyHistoryAsync();

        Assert.Equal(3, copied.Count);  // vanilla scores of Alice, Bob, Carol; relax stays in TomYum
        Assert.Empty(again);            // nothing copied twice
        var alice = await IdOfAsync("alice");
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT COUNT(*) FROM scores WHERE user_id = {alice}"));
        Assert.Equal(700.0, await ScalarAsync<double>($"SELECT rank_score FROM osu_user_stats WHERE user_id = {alice}"), precision: 1);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT rank_score_index FROM osu_user_stats WHERE user_id = {alice}"));
    }

    /// <summary>
    /// A TomYum server with its own accounts: #2 dev seed, #3 Alice (Owner, JP), #4 Bob Smith,
    /// #5 Carol (already on osu-web as #2 with the same email), plus orphan data for #9.
    /// </summary>
    private async Task<TomYumDbContext> SeedServerAsync()
    {
        await ExecuteAsync(
            "INSERT INTO phpbb_users (user_id, username, username_clean, user_email, user_sig, user_occ, user_interests, group_id) " +
            "VALUES (2, 'Carol', 'carol', 'carol@example.com', '', '', '', 7)");

        var tomyum = await NewTomYumContextAsync();
        tomyum.Users.AddRange(
            User(2, "TomYum", OsuWebAccountMigration.DevSeedEmail),
            User(3, "Alice", "alice@example.com", country: "jp", privileges: UserPrivileges.Owner,
                hash: AliceHash),
            User(4, "Bob Smith", "bob@example.com"),
            User(5, "Carol", "CAROL@example.com"));
        tomyum.Beatmaps.Add(new Beatmap
        {
            Id = MapId, SetId = MapId, Md5 = "md5-map", Artist = "A", Title = "T", Version = "V", Creator = "C",
            Status = RankedStatus.Ranked, TotalLength = 90, LastUpdate = DateTimeOffset.UtcNow,
        });
        tomyum.Scores.AddRange(Score(2), Score(3), Score(4), Score(5), Score(9), Score(3, mode: GameMode.RelaxOffset));
        tomyum.UserStats.AddRange(
            new UserStats { UserId = 3, Mode = 0, Pp = 700, Accuracy = 0.99f, PlayCount = 1, RankedScore = 1, TotalScore = 1 },
            new UserStats { UserId = 9, Mode = 0, Pp = 50 });
        tomyum.UserAchievements.Add(new UserAchievement { UserId = 3, AchievementId = 1, UnlockedAt = DateTime.UtcNow });
        await tomyum.SaveChangesAsync();
        return tomyum;
    }

    private OsuWebAccountMigration NewMigration(TomYumDbContext tomyum) =>
        new(tomyum, new OsuWebUserRepository(NewOsuWebContext()));

    private async Task<int> IdOfAsync(string usernameClean) =>
        await ScalarAsync<int>($"SELECT user_id FROM phpbb_users WHERE username_clean = '{usernameClean}'");

    private static AccountMigrationEntry Entry(AccountMigrationPlan plan, int oldId) => plan.Entries.Single(e => e.OldId == oldId);

    private static User User(int id, string name, string email, string country = "xx", UserPrivileges privileges = UserPrivileges.Normal,
        string? hash = null) => new()
    {
        Id = id,
        Username = name,
        SafeName = Domain.Entities.User.MakeSafeName(name),
        Email = email,
        PasswordBcrypt = hash ?? OtherHash,
        Country = country,
        CreationTime = 1_700_000_000,
        LatestActivity = 1_700_000_000,
        Privileges = privileges,
    };

    private static Score Score(int userId, int mode = 0) => new()
    {
        UserId = userId,
        BeatmapId = MapId,
        BeatmapMd5 = "md5-map",
        Mode = mode,
        ScoreValue = 500_000,
        MaxCombo = 300,
        Count300 = 200,
        Grade = "A",
        Passed = true,
        Pp = 70,
        Status = ScoreStatus.Best,
        SubmittedAt = DateTimeOffset.UtcNow,
    };
}
