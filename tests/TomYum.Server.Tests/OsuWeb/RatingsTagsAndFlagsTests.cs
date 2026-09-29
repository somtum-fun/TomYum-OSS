using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using TomYum.Server.Admin;
using TomYum.Server.Anticheat;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Map ratings from osu!stable, osu!'s beatmap tags, and the client's hidden anti-cheat flags.</summary>
public sealed class RatingsTagsAndFlagsTests : OsuWebDatabaseTest
{
    [MySqlFact]
    public async Task Ratings_OnePerPlayer_AndTheSetKeepsTheAverage()
    {
        await ExecuteAsync("INSERT INTO osu_beatmapsets (beatmapset_id, artist, title, creator) VALUES (10, 'A', 'T', 'M')");
        var website = new OsuWebStableClient(NewOsuWebContext());

        Assert.Equal((false, 0.0), await website.RatingAsync(1, 10));
        Assert.Equal(8.0, await website.RateAsync(1, 10, 8));
        Assert.Equal(8.0, await website.RateAsync(1, 10, 2));   // one vote each: the second is ignored
        Assert.Equal(6.5, await website.RateAsync(2, 10, 5));
        Assert.Equal((true, 6.5), await website.RatingAsync(1, 10));
        Assert.Equal(6.5, await ScalarAsync<double>("SELECT rating FROM osu_beatmapsets WHERE beatmapset_id = 10"));
    }

    [MySqlFact]
    public async Task Tags_FromOsu_AreAddedAndUpdated_KeepingTheirIds()
    {
        var parsed = OsuApiV2Client.ParseTags(JsonDocument.Parse("""
            {"tags":[
              {"id":3,"name":"style/clean","ruleset_id":null,"description":"Clean mapping."},
              {"id":7,"name":"skill/aim","ruleset_id":0,"description":"Jumps."},
              {"name":"no id"}
            ]}
            """).RootElement);
        Assert.Equal([new BeatmapTagDefinition(3, "style/clean", null, "Clean mapping."), new BeatmapTagDefinition(7, "skill/aim", 0, "Jumps.")], parsed);

        var tags = new OsuWebTags(NewOsuWebContext());
        Assert.Equal(2, await tags.UpsertAsync(parsed));
        Assert.Equal(0, await tags.UpsertAsync(parsed));   // nothing new
        Assert.Equal(1, await tags.UpsertAsync([parsed[0] with { Description = "Tidy mapping." }]));
        Assert.Equal("Tidy mapping.", await ScalarAsync<string>("SELECT description FROM tags WHERE id = 3"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM tags"));
    }

    [Theory]
    [InlineData("12345", null)]
    [InlineData("", null)]
    [InlineData("a0", LastFmSignals.None)]
    [InlineData("a131072", LastFmSignals.HqAssembly)]
    [InlineData("a589824", LastFmSignals.RegistryEdits | LastFmSignals.ExtraThreads)]
    public void Flags_AreOnlyTheAPrefixedValues(string value, LastFmSignals? expected) =>
        Assert.Equal(expected, ClientFlags.Parse(value));

    [MySqlFact]
    public async Task HqOsu_Restricts_AndRegistryTraces_Warn()
    {
        await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var cheater = await users.AddAsync(new User { Username = "Cheater", SafeName = "cheater", Email = "c@x.dev", PasswordBcrypt = "x", Country = "nl" });
        var relife = await users.AddAsync(new User { Username = "Relife", SafeName = "relife", Email = "r@x.dev", PasswordBcrypt = "x", Country = "nl" });
        var registry = new PlayerRegistry();
        var online = registry.Add(new BanchoPresence { UserId = relife.Id, Username = "Relife" });
        using var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(registry)
            .AddSingleton<IUserRepository>(_ => new OsuWebUserRepository(NewOsuWebContext()))
            .AddSingleton(_ => new OsuWebModeration(NewOsuWebContext()))
            .AddSingleton<IStaffLogRepository>(_ => new StaffLogRepository(NewTomYumContext()))
            .AddSingleton<RestrictedUsers>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ILogger<UserModerationSync>>(NullLogger<UserModerationSync>.Instance)
            .AddSingleton<UserModerationSync>()
            .BuildServiceProvider();

        await ClientFlags.HandleAsync(services, cheater, LastFmSignals.HqFile, CancellationToken.None);
        await ClientFlags.HandleAsync(services, relife, LastFmSignals.RegistryEdits, CancellationToken.None);

        Assert.True((await users.GetByIdAsync(cheater.Id))!.IsRestricted);
        Assert.False((await users.GetByIdAsync(relife.Id))!.IsRestricted);
        Assert.Contains((ushort)ServerPacketId.Notification, PacketIds(online.DequeueAll()));
        var log = await services.GetRequiredService<IStaffLogRepository>().ListAsync(new StaffLogQuery(Limit: 10));
        Assert.Contains(log, e => e.Action == "user.restrict" && e.TargetId == cheater.Id);
        Assert.Contains(log, e => e.Action == ClientFlags.RegistryAction && e.TargetId == relife.Id);
    }

    private static List<ushort> PacketIds(byte[] body)
    {
        var ids = new List<ushort>();
        var reader = new BanchoPacketReader(body);
        while (reader.CanReadHeader)
        {
            var header = reader.ReadHeader();
            ids.Add((ushort)header.Id);
            reader.Skip(checked((int)header.Length));
        }
        return ids;
    }
}
