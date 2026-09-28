using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Services;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Bancho.Packets.Match;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Admin;

/// <summary>
/// What a restricted player can't do (multiplayer) and the shadow leaderboard: their scores are
/// hidden from everyone else but shown to them, in place, once their password checks out.
/// </summary>
public sealed class RestrictedPlayTests
{
    private const string Md5 = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Restricted_CantCreateAMatch()
    {
        var (registry, matches, player) = Setup(restricted: true);

        var context = new ClientPacketContext(player, registry, new ChannelRegistry(), matches, new ServiceCollection().BuildServiceProvider(), ReadOnlyMemory<byte>.Empty, new BanchoPacketWriter(64));
        await new CreateMatchHandler().HandleAsync(context);

        Assert.Equal([(ushort)ServerPacketId.Notification, (ushort)ServerPacketId.MatchJoinFail], PacketIds(context.Response.ToArray()));
        Assert.Empty(matches.All);
    }

    [Fact]
    public async Task Restricted_CantJoinAMatch()
    {
        var (registry, matches, player) = Setup(restricted: true);
        var match = matches.Create(99, "open room", string.Empty, "map", 1, Md5, 0, 0, 0, false, 0, 0);
        var payload = new BanchoPacketWriter(16).WriteInt32(match.Id).WriteString(string.Empty).ToArray();

        var context = new ClientPacketContext(player, registry, new ChannelRegistry(), matches, new ServiceCollection().BuildServiceProvider(), payload, new BanchoPacketWriter(64));
        await new JoinMatchHandler().HandleAsync(context);

        Assert.Equal([(ushort)ServerPacketId.Notification, (ushort)ServerPacketId.MatchJoinFail], PacketIds(context.Response.ToArray()));
        Assert.DoesNotContain(match.Slots, s => s.UserId == player.UserId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheLobby_IsEmptyForRestrictedPlayers(bool restricted)
    {
        var (registry, matches, player) = Setup(restricted);
        matches.Create(99, "open room", string.Empty, "map", 1, Md5, 0, 0, 0, false, 0, 0);

        var context = new ClientPacketContext(player, registry, new ChannelRegistry(), matches, new ServiceCollection().BuildServiceProvider(), ReadOnlyMemory<byte>.Empty, new BanchoPacketWriter(64));
        await new JoinLobbyHandler().HandleAsync(context);

        var packets = PacketIds(context.Response.ToArray());
        Assert.Equal(restricted ? [(ushort)ServerPacketId.Notification] : [(ushort)ServerPacketId.NewMatch], packets);
        Assert.Equal(!restricted, player.InLobby);
    }

    [Fact]
    public async Task Leaderboard_HidesRestrictedPlayers_ExceptFromThemselves()
    {
        var restricted = new RestrictedUsers();
        var scores = new InMemoryScoreRepository(restricted);
        await scores.AddAsync(Best(1, 1_000_000));
        await scores.AddAsync(Best(2, 900_000));
        restricted.Set(1, true);

        Assert.Equal([2], (await scores.GetLeaderboardAsync(Md5, 0, 50)).Select(s => s.UserId));
        Assert.Equal([2], (await scores.GetLeaderboardAsync(Md5, 0, 50, viewerId: 2)).Select(s => s.UserId));
        Assert.Equal([1, 2], (await scores.GetLeaderboardAsync(Md5, 0, 50, viewerId: 1)).Select(s => s.UserId));
    }

    [Fact]
    public async Task GetScores_ShowsARestrictedPlayerTheirScore_OnlyWithTheirPassword()
    {
        var hasher = new BcryptPasswordHasher();
        var users = new InMemoryUserRepository();
        var cheater = await users.AddAsync(new User { Username = "Cheater", Email = "c@x.dev", PasswordBcrypt = hasher.HashPassword(hasher.ComputeMd5Hex("secret")) });
        var honest = await users.AddAsync(new User { Username = "Honest", Email = "h@x.dev", PasswordBcrypt = hasher.HashPassword(hasher.ComputeMd5Hex("other")) });
        var restricted = new RestrictedUsers();
        restricted.Set(cheater.Id, true);
        var scores = new InMemoryScoreRepository(restricted);
        await scores.AddAsync(Best(cheater.Id, 1_000_000));
        await scores.AddAsync(Best(honest.Id, 900_000));

        async Task<string[]> RequestAsync(string username, string password)
        {
            var context = new DefaultHttpContext();
            context.Request.QueryString = QueryString.Create(new Dictionary<string, string?>
            {
                ["c"] = Md5, ["m"] = "0", ["mods"] = "0", ["us"] = username, ["ha"] = password.Length > 0 ? hasher.ComputeMd5Hex(password) : string.Empty,
            });
            var result = await WebEndpoints.HandleGetScoresAsync(
                context, new ConfigurationBuilder().Build(), new BeatmapService(new OneMap()), scores, users, new PlayerRegistry(), hasher, restricted, CancellationToken.None);
            return ((ContentHttpResult)result).ResponseContent!.Split('\n', StringSplitOptions.TrimEntries);
        }

        // Lines: header, 0, name, rating, personal best, then the board.
        var own = await RequestAsync("Cheater", "secret");
        Assert.Contains("|Cheater|", own[4], StringComparison.Ordinal);
        Assert.Contains("|Cheater|", own[5], StringComparison.Ordinal);
        Assert.Equal("1", own[4].Split('|')[13]); // their personal best, ranked #1 as for anyone
        Assert.Contains("|Honest|", own[6], StringComparison.Ordinal);

        foreach (var (name, password) in new[] { ("Cheater", "wrong"), ("Cheater", string.Empty), ("Honest", "other"), (string.Empty, string.Empty) })
        {
            var lines = await RequestAsync(name, password);
            Assert.DoesNotContain(lines, l => l.Contains("|Cheater|", StringComparison.Ordinal));
            Assert.Contains("|Honest|", lines[5], StringComparison.Ordinal);
        }
    }

    private static (PlayerRegistry, MatchRegistry, OnlinePlayer) Setup(bool restricted)
    {
        var registry = new PlayerRegistry();
        var player = registry.Add(new BanchoPresence { UserId = 7, Username = "Player" });
        player.IsRestricted = restricted;
        return (registry, new MatchRegistry(), player);
    }

    private static Score Best(int userId, long score) => new()
    {
        UserId = userId,
        BeatmapId = 1,
        BeatmapMd5 = Md5,
        Mode = 0,
        ScoreValue = score,
        Grade = "A",
        Passed = true,
        Status = ScoreStatus.Best,
        SubmittedAt = DateTimeOffset.UtcNow,
    };

    private static List<ushort> PacketIds(byte[] body)
    {
        var ids = new List<ushort>();
        var reader = new BanchoPacketReader(body);
        while (reader.CanReadHeader)
        {
            var header = reader.ReadHeader();
            ids.Add((ushort)header.Id);
            reader.ReadBytes((int)header.Length);
        }
        return ids;
    }

    private sealed class OneMap : IOsuApiClient
    {
        public bool SupportsSince => false;

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) =>
            Task.FromResult<Beatmap?>(md5 == Md5
                ? new Beatmap { Id = 1, SetId = 1, Md5 = Md5, Status = RankedStatus.Ranked, Artist = "A", Title = "T", Version = "V", Creator = "C" }
                : null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);
    }
}
