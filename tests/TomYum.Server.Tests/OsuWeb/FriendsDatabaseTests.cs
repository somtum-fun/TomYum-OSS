using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Friends and favourites in the website's tables, and the filtered leaderboard query.</summary>
public sealed class FriendsDatabaseTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task Friends_AreTheWebsites_AndReplaceABlock()
    {
        // 1 already blocked 3 on the website; 2 has friended 1 (one-way, so not 1's friend).
        await ExecuteAsync("INSERT INTO phpbb_zebra (user_id, zebra_id, friend, foe) VALUES (1, 3, 0, 1), (2, 1, 1, 0)");
        var friends = new OsuWebFriends(NewOsuWebContext());

        await friends.AddAsync(1, 2);
        await friends.AddAsync(1, 2);
        await friends.AddAsync(1, 3);
        Assert.Equal([2, 3], (await friends.GetFriendIdsAsync(1)).Order());
        Assert.Equal(0, await ScalarAsync<int>("SELECT foe FROM phpbb_zebra WHERE user_id = 1 AND zebra_id = 3"));

        await friends.RemoveAsync(1, 2);
        Assert.Equal([3], await friends.GetFriendIdsAsync(1));
        Assert.Equal([1], await friends.GetFriendIdsAsync(2));
    }

    [MySqlFact]
    public async Task Favourites_AreListedNewestFirst()
    {
        await ExecuteAsync("INSERT INTO osu_favouritemaps (user_id, beatmapset_id, dateadded) VALUES (5, 100, '2026-01-01'), (5, 200, '2026-02-01'), (6, 300, '2026-03-01')");

        Assert.Equal([200, 100], await new OsuWebStableClient(NewOsuWebContext()).FavouriteSetIdsAsync(5));
    }

    [MySqlFact]
    public async Task Leaderboard_FiltersByModsAndPlayers()
    {
        await NewTomYumContextAsync();
        var scores = new ScoreRepository(NewTomYumContext());
        foreach (var (user, value, mods) in new[] { (1, 100, 0), (2, 200, 8), (3, 300, 8), (4, 400, 0) })
            await scores.AddAsync(new Score { UserId = user, BeatmapMd5 = "md5", Mode = 0, ScoreValue = value, Mods = mods, Status = ScoreStatus.Best, Passed = true, Grade = "A" });

        Assert.Equal([3, 2], (await scores.GetLeaderboardAsync("md5", 0, 50, null, new LeaderboardFilter(Mods: 8))).Select(s => s.UserId));
        Assert.Equal([4, 1], (await scores.GetLeaderboardAsync("md5", 0, 50, null, new LeaderboardFilter(UserIds: [1, 4]))).Select(s => s.UserId));
        Assert.Equal([2], (await scores.GetLeaderboardAsync("md5", 0, 50, null, new LeaderboardFilter(8, [1, 2]))).Select(s => s.UserId));
    }
}
