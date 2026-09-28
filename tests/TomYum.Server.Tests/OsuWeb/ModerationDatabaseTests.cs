using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using TomYum.Server.Admin;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Restrictions, silences, score removal and the chat log against real databases.</summary>
public sealed class ModerationDatabaseTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task Accounts_CarryRestrictionAndSilence_AsOsuWebDecidesThem()
    {
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var normal = await users.AddAsync(NewUser("Normal"));
        var warned = await users.AddAsync(NewUser("Warned"));
        var silenced = await users.AddAsync(NewUser("Silenced"));
        var lifted = await users.AddAsync(NewUser("Lifted"));
        await ExecuteAsync($"UPDATE phpbb_users SET user_warnings = 1 WHERE user_id = {warned.Id}");
        // Silenced an hour ago for a day; the other one was silenced, then lifted (period 0) later.
        await ExecuteAsync($"""
            INSERT INTO osu_user_banhistory (user_id, ban_status, period, `timestamp`) VALUES
              ({silenced.Id}, 2, 86400, UTC_TIMESTAMP() - INTERVAL 1 HOUR),
              ({lifted.Id}, 2, 86400, UTC_TIMESTAMP() - INTERVAL 2 HOUR),
              ({lifted.Id}, 2, 0, UTC_TIMESTAMP() - INTERVAL 1 HOUR),
              ({normal.Id}, 0, 0, UTC_TIMESTAMP())
            """);

        Assert.False((await users.GetByIdAsync(normal.Id))!.IsRestricted);
        Assert.Null((await users.GetByIdAsync(normal.Id))!.SilenceEndUtc);   // a note isn't a silence
        Assert.True((await users.GetByIdAsync(warned.Id))!.IsRestricted);
        Assert.Null((await users.GetByIdAsync(lifted.Id))!.SilenceEndUtc);
        var end = (await users.GetByIdAsync(silenced.Id))!.SilenceEndUtc;
        Assert.NotNull(end);
        Assert.InRange(end!.Value, DateTimeOffset.UtcNow.AddHours(22.9), DateTimeOffset.UtcNow.AddHours(23.1));

        Assert.Equal([warned.Id], await users.GetRestrictedIdsAsync());
    }

    [MySqlFact]
    public async Task Leaderboards_AndRanks_LeaveRestrictedPlayersOut()
    {
        await using var tomyum = await NewTomYumContextAsync();
        await AddScoresAsync(tomyum, Play(10, 1, 2_000_000), Play(11, 1, 1_000_000));
        tomyum.UserStats.AddRange(new UserStats { UserId = 10, Mode = 0, Pp = 500 }, new UserStats { UserId = 11, Mode = 0, Pp = 100 });
        await tomyum.SaveChangesAsync();
        var restricted = new RestrictedUsers();
        restricted.Set(10, true);

        var board = await new ScoreRepository(tomyum, restricted).GetLeaderboardAsync("md5-1", 0, 50);

        Assert.Equal([11], board.Select(s => s.UserId));
        // The shadow leaderboard: the restricted player still sees their own score, in place.
        Assert.Equal([10, 11], (await new ScoreRepository(tomyum, restricted).GetLeaderboardAsync("md5-1", 0, 50, viewerId: 10)).Select(s => s.UserId));
        Assert.Equal([11], (await new ScoreRepository(tomyum, restricted).GetLeaderboardAsync("md5-1", 0, 50, viewerId: 11)).Select(s => s.UserId));
        var stats = new UserStatsRepository(tomyum, restricted);
        Assert.Equal(1, await stats.GetGlobalRankAsync(11, 0));   // #1 once the restricted player is out
        Assert.Equal(0, await stats.GetGlobalRankAsync(10, 0));   // unranked
    }

    [MySqlFact]
    public async Task RemovingABest_PromotesTheNextOne_AndRecomputesStats()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var best = Play(2, 1, 1_000_000, pp: 200);
        var older = Play(2, 1, 900_000, pp: 150, status: ScoreStatus.Submitted);
        var other = Play(2, 2, 500_000, pp: 100);
        await AddScoresAsync(tomyum, best, older, other);
        tomyum.UserStats.Add(new UserStats { UserId = 2, Mode = 0, Pp = 999, RankedScore = 1_500_000, TotalScore = 2_400_000, PlayCount = 3 });
        await tomyum.SaveChangesAsync();
        tomyum.ChangeTracker.Clear();

        var result = await NewModeration(tomyum).RemoveAsync(best.Id, actorId: 5, reason: "cheated");

        Assert.Equal(1, result!.Removed);
        var remaining = await tomyum.Scores.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
        Assert.Equal([ScoreStatus.Best, ScoreStatus.Best], remaining.Select(s => s.Status));   // the 900k took over
        var stats = await tomyum.UserStats.AsNoTracking().SingleAsync();
        Assert.Equal(1_400_000, stats.RankedScore);
        Assert.Equal(1_400_000, stats.TotalScore);
        Assert.Equal(2, stats.PlayCount);
        Assert.Equal(PerformanceTotals.Compute([(150, 0), (100, 0)])!.Value.Pp, stats.Pp, 3);

        var entry = Assert.Single(await new StaffLogRepository(tomyum).ListAsync(new StaffLogQuery()));
        Assert.Equal(("score.remove", 2L, 5), (entry.Action, entry.TargetId, entry.ActorId));
        Assert.EndsWith("Reason: cheated", entry.Summary, StringComparison.Ordinal);

        Assert.Null(await NewModeration(tomyum).RemoveAsync(best.Id, 5, null));
    }

    [MySqlFact]
    public async Task Wipe_RemovesOneModeOrEverything_AndTheWebsiteCopies()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var player = await users.AddAsync(NewUser("Wiped"));
        var beatmaps = new OsuWebBeatmapMirror(NewOsuWebContext());
        await beatmaps.WriteAsync(new Beatmap { Id = 1, SetId = 1, Md5 = "md5-1", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "x", Artist = "a", Title = "t", Creator = "c" });
        var osu = Play(player.Id, 1, 1_000_000);
        var taiko = Play(player.Id, 1, 800_000, mode: 1);
        await AddScoresAsync(tomyum, osu, taiko);
        tomyum.UserStats.AddRange(new UserStats { UserId = player.Id, Mode = 0, PlayCount = 1 }, new UserStats { UserId = player.Id, Mode = 1, PlayCount = 1 });
        await tomyum.SaveChangesAsync();
        tomyum.ChangeTracker.Clear();
        var mirror = new OsuWebScoreMirror(tomyum, NewOsuWebContext());
        await mirror.EnsureLinkTableAsync();
        await mirror.WriteScoreAsync(osu);
        await mirror.WriteScoreAsync(taiko);
        Assert.Equal(2L, await ScalarAsync<long>("SELECT COUNT(*) FROM scores"));

        var moderation = NewModeration(tomyum, mirror);
        Assert.Equal(1, (await moderation.WipeAsync(player.Id, mode: 1, actorId: 5, reason: null)).Removed);
        Assert.Equal([0], await tomyum.Scores.AsNoTracking().Select(s => s.Mode).ToListAsync());
        Assert.Equal(1L, await ScalarAsync<long>("SELECT COUNT(*) FROM scores"));

        Assert.Equal(1, (await moderation.WipeAsync(player.Id, mode: null, actorId: 5, reason: null)).Removed);
        Assert.Empty(await tomyum.Scores.AsNoTracking().ToListAsync());
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM scores"));
    }

    [MySqlFact]
    public async Task ChatLog_FiltersByUserChannelAndConversation()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var chat = new ChatLogRepository(tomyum);
        await chat.AddRangeAsync(
        [
            Line(2, "Alice", "#osu", null, "hi all"),
            Line(3, "Bob", "#osu", null, "hey"),
            Line(2, "Alice", "Bob", 3, "psst"),
            Line(3, "Bob", "Alice", 2, "what"),
            Line(4, "Carol", "#lobby", null, "mp?"),
        ]);

        Assert.Equal(5, (await chat.ListAsync(new ChatLogQuery())).Count);
        Assert.Equal(["what", "psst", "hi all"], (await chat.ListAsync(new ChatLogQuery(UserId: 2))).Select(m => m.Text));
        Assert.Equal(["hey", "hi all"], (await chat.ListAsync(new ChatLogQuery(Target: "#osu"))).Select(m => m.Text));
        Assert.Equal(["what", "psst"], (await chat.ListAsync(new ChatLogQuery(Target: "Bob"))).Select(m => m.Text));

        tomyum.ChatLog.Add(Line(2, "Alice", "#osu", null, "ancient", sentAt: DateTime.UtcNow.AddDays(-40)));
        await tomyum.SaveChangesAsync();
        Assert.Equal(1, await chat.DeleteOlderThanAsync(DateTime.UtcNow - ChatLog.Retention));
    }

    private static ScoreModeration NewModeration(TomYumDbContext tomyum, OsuWebScoreMirror? mirror = null) => new(
        tomyum, new StaffLogRepository(tomyum), new ConfigurationBuilder().Build(), TimeProvider.System,
        NullLogger<ScoreModeration>.Instance, mirror);

    private static async Task AddScoresAsync(TomYumDbContext tomyum, params Score[] scores)
    {
        tomyum.Scores.AddRange(scores);
        await tomyum.SaveChangesAsync();
        tomyum.ChangeTracker.Clear();
    }

    private static Score Play(int userId, int beatmapId, long value, double pp = 100, int mode = 0, int status = ScoreStatus.Best) => new()
    {
        UserId = userId,
        BeatmapId = beatmapId,
        BeatmapMd5 = $"md5-{beatmapId}",
        Mode = mode,
        ScoreValue = value,
        MaxCombo = 500,
        Count300 = 480,
        Grade = "S",
        Passed = true,
        Pp = pp,
        Status = status,
        SubmittedAt = DateTimeOffset.UtcNow,
    };

    private static ChatLogEntry Line(int sender, string name, string target, int? recipient, string text, DateTime? sentAt = null) => new()
    {
        SentAt = sentAt ?? DateTime.UtcNow,
        SenderId = sender,
        SenderName = name,
        Target = target,
        RecipientId = recipient,
        Text = text,
    };

    private static User NewUser(string name) => new()
    {
        Username = name,
        SafeName = User.MakeSafeName(name),
        Email = $"{name.ToLowerInvariant()}@example.com",
        PasswordBcrypt = new BcryptPasswordHasher().HashPassword("x"),
        Country = "th",
    };
}
