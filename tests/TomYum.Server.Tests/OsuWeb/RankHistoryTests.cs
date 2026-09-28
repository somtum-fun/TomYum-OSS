using System.Globalization;
using TomYum.Domain.Entities;
using TomYum.Persistence.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>The daily rank snapshot behind the website's profile rank graph (<see cref="OsuWebRankHistory"/>).</summary>
public sealed class RankHistoryTests : OsuWebDatabaseTest
{
    private static readonly DateTimeOffset Day1 = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [MySqlFact]
    public async Task EachDay_GoesInTheNextColumn_OncePerDay()
    {
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var alpha = (await users.AddAsync(NewUser("Alpha"))).Id;
        var bravo = (await users.AddAsync(NewUser("Bravo"))).Id;
        var cheater = (await users.AddAsync(NewUser("Cheater"))).Id;
        var idle = (await users.AddAsync(NewUser("Idle"))).Id;
        await ExecuteAsync($"UPDATE phpbb_users SET user_warnings = 1 WHERE user_id = {cheater}");
        await StatsAsync(alpha, 500);
        await StatsAsync(bravo, 300);
        await StatsAsync(cheater, 9000);
        await StatsAsync(idle, 0);
        var history = new OsuWebRankHistory(NewOsuWebContext());

        Assert.True(await history.RecordIfDueAsync(Day1));
        Assert.False(await history.RecordIfDueAsync(Day1.AddHours(10))); // same UTC day

        Assert.Equal(0L, await ScalarAsync<long>("SELECT count FROM osu_counts WHERE name = 'pp_rank_column_osu'"));
        Assert.Equal(1, await RankAsync(alpha, 0));
        Assert.Equal(2, await RankAsync(bravo, 0));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT COUNT(*) FROM osu_user_performance_rank WHERE user_id IN ({cheater}, {idle})"));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_user_performance_rank WHERE mode <> 0")); // nobody plays the rest

        // Next day Bravo overtakes: the next column, and Alpha's best rank stays #1.
        await ExecuteAsync($"UPDATE osu_user_stats SET rank_score = 800 WHERE user_id = {bravo}");
        Assert.True(await history.RecordIfDueAsync(Day1.AddDays(1)));
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count FROM osu_counts WHERE name = 'pp_rank_column_osu'"));
        Assert.Equal((1, 2), (await RankAsync(alpha, 0), await RankAsync(alpha, 1)));
        Assert.Equal((2, 1), (await RankAsync(bravo, 0), await RankAsync(bravo, 1)));
        Assert.Equal(1, await ScalarAsync<int>($"SELECT `rank` FROM osu_user_performance_rank_highest WHERE user_id = {alpha} AND mode = 0"));
        Assert.Equal(1, await ScalarAsync<int>($"SELECT `rank` FROM osu_user_performance_rank_highest WHERE user_id = {bravo} AND mode = 0"));
    }

    [MySqlFact]
    public async Task TheColumns_WrapAfter90Days_AndDropPlayersWhoLostTheirRank()
    {
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var alpha = (await users.AddAsync(NewUser("Alpha"))).Id;
        await StatsAsync(alpha, 500);
        await ExecuteAsync("INSERT INTO osu_counts (name, count) VALUES ('pp_rank_column_osu', 89)");
        await ExecuteAsync($"INSERT INTO osu_user_performance_rank (user_id, mode, r0) VALUES ({alpha}, 0, 42)");

        await new OsuWebRankHistory(NewOsuWebContext()).RecordAsync(0);
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count FROM osu_counts WHERE name = 'pp_rank_column_osu'"));
        Assert.Equal(1, await RankAsync(alpha, 0)); // day 90 ago overwritten

        await ExecuteAsync($"UPDATE phpbb_users SET user_warnings = 1 WHERE user_id = {alpha}");
        await new OsuWebRankHistory(NewOsuWebContext()).RecordAsync(0);
        Assert.Equal(0, await RankAsync(alpha, 1)); // restricted: no rank that day
    }

    private Task<int> RankAsync(int userId, int column) =>
        ScalarAsync<int>($"SELECT r{column.ToString(CultureInfo.InvariantCulture)} FROM osu_user_performance_rank WHERE user_id = {userId} AND mode = 0");

    private Task StatsAsync(int userId, double pp) => ExecuteAsync(string.Create(CultureInfo.InvariantCulture, $"""
        INSERT INTO osu_user_stats (user_id, accuracy_total, accuracy_count, accuracy, playcount, ranked_score, total_score,
          x_rank_count, s_rank_count, a_rank_count, `rank`, level, country_acronym, rank_score, rank_score_index, accuracy_new)
        VALUES ({userId}, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 'NL', {pp}, 0, 90)
        """));

    private static User NewUser(string name) => new()
    {
        Username = name,
        SafeName = User.MakeSafeName(name),
        Email = $"{name.ToLowerInvariant()}@x.dev",
        PasswordBcrypt = "x",
        Country = "nl",
    };
}
