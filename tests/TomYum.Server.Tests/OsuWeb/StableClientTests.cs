using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Performance;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>What osu!stable's song select and screenshot button ask for, and custom sets' graveyard.</summary>
public sealed class StableClientTests : OsuWebDatabaseTest, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tomyum-stable-" + Guid.NewGuid().ToString("N"));
    private readonly BcryptPasswordHasher _hasher = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void FileNames_AreOsusOwn() =>
        Assert.Equal("Acathexis - Stillborn  Isolate (bighzonda) [Slumber].osu", OsuFileName.For("Acathexis", "Stillborn // Isolate", "bighzonda", "Slumber"));

    [MySqlFact]
    public async Task SongSelect_GetsStatusesAndGrades_ByFileName_AndFavouritesStick()
    {
        var (services, player) = await SetUpAsync();
        await ExecuteAsync($"""
            INSERT INTO scores (user_id, ruleset_id, beatmap_id, preserve, ranked, `rank`, passed, accuracy, max_combo, total_score, data, pp, ended_at)
            VALUES ({player}, 0, 100, 1, 1, 'A', 1, 0.9, 100, 500000, '{Data}', 10, UTC_TIMESTAMP()),
                   ({player}, 0, 100, 1, 1, 'S', 1, 0.97, 100, 900000, '{Data}', 20, UTC_TIMESTAMP())
            """);

        var context = Request(services, "u=Player&h=" + _hasher.ComputeMd5Hex("pw"));
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"Filenames":["nope.osu","Artist - Song (Mapper) [Hard].osu"],"Ids":[]}"""));
        var answer = Assert.IsType<ContentHttpResult>(await StableClientEndpoints.HandleBeatmapInfoAsync(context, default));
        Assert.Equal("1|100|10|md5-100|1|S|N|N|N\n", answer.ResponseContent);

        var wrong = Request(services, "u=Player&h=" + _hasher.ComputeMd5Hex("nope"));
        wrong.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"Filenames":[]}"""));
        Assert.IsType<UnauthorizedHttpResult>(await StableClientEndpoints.HandleBeatmapInfoAsync(wrong, default));

        var favourite = Request(services, "u=Player&h=" + _hasher.ComputeMd5Hex("pw") + "&a=10");
        Assert.Equal(string.Empty, Assert.IsType<ContentHttpResult>(await StableClientEndpoints.HandleAddFavouriteAsync(favourite, default)).ResponseContent);
        Assert.Contains("already", Assert.IsType<ContentHttpResult>(await StableClientEndpoints.HandleAddFavouriteAsync(favourite, default)).ResponseContent, StringComparison.Ordinal);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT favourite_count FROM osu_beatmapsets WHERE beatmapset_id = 10"));
    }

    [MySqlFact]
    public async Task OutdatedMaps_AreOfferedTheirNewFile()
    {
        var (services, _) = await SetUpAsync();
        var files = new OsuFileCache(Path.Combine(_directory, "osu"), new HttpClient(), NullLogger<OsuFileCache>.Instance);

        // An official map: osu! has its new file.
        var official = Assert.IsType<RedirectHttpResult>(await StableClientEndpoints.HandleMapFileAsync("Artist - Song (Mapper) [Hard].osu", Request(services, string.Empty), files, default));
        Assert.Equal("https://osu.ppy.sh/web/maps/Artist%20-%20Song%20%28Mapper%29%20%5BHard%5D.osu", official.Url);

        // A custom one: this server's copy.
        await new OsuWebBeatmapMirror(NewOsuWebContext()).WriteAsync(new Beatmap
        {
            Id = Beatmap.CustomIdFloor + 1, SetId = Beatmap.CustomIdFloor, Md5 = "c", Status = RankedStatus.Pending, OsuApiStatus = 0,
            Version = "Mine", Artist = "Me", Title = "Tune", Creator = "Player",
        });
        Directory.CreateDirectory(Path.Combine(_directory, "osu"));
        await File.WriteAllTextAsync(Path.Combine(_directory, "osu", $"{Beatmap.CustomIdFloor + 1}.osu"), "osu file format v14");
        var custom = Assert.IsType<FileContentHttpResult>(await StableClientEndpoints.HandleMapFileAsync("Me - Tune (Player) [Mine].osu", Request(services, string.Empty), files, default));
        Assert.Equal("osu file format v14", Encoding.UTF8.GetString(custom.FileContents.Span));
    }

    [MySqlFact]
    public async Task Screenshots_AreKept_AndShown()
    {
        var (services, _) = await SetUpAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Screenshots:Path"] = Path.Combine(_directory, "ss") }).Build();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.ContentType = "multipart/form-data; boundary=x";
        context.Request.Form = new FormCollection(
            new Dictionary<string, StringValues> { ["u"] = "Player", ["p"] = _hasher.ComputeMd5Hex("pw") },
            new FormFileCollection { new FormFile(new MemoryStream(png), 0, png.Length, "ss", "ss.png") });

        var name = Assert.IsType<ContentHttpResult>(await StableClientEndpoints.HandleScreenshotAsync(context, configuration, default)).ResponseContent!;
        Assert.Matches(@"^[0-9a-f]{12}\.png$", name);
        Assert.IsType<PhysicalFileHttpResult>(StableClientEndpoints.HandleScreenshotFile(name, configuration));
        Assert.IsType<NotFound>(StableClientEndpoints.HandleScreenshotFile("../secret.png", configuration));
    }

    [MySqlFact]
    public async Task UntouchedCustomSets_GoToTheGraveyard_AndComeBackWhenUpdated()
    {
        await NewTomYumContextAsync();
        var mirror = new OsuWebBeatmapMirror(NewOsuWebContext());
        foreach (var (id, set) in new[] { (Beatmap.CustomIdFloor + 1, Beatmap.CustomIdFloor), (Beatmap.CustomIdFloor + 2, Beatmap.CustomIdFloor + 1) })
        {
            await mirror.WriteAsync(new Beatmap
            {
                Id = id, SetId = set, Md5 = "m" + id.ToString(CultureInfo.InvariantCulture), Status = RankedStatus.Pending, OsuApiStatus = 0,
                Version = "V", Artist = "A", Title = "T", Creator = "C",
            });
        }
        await ExecuteAsync($"UPDATE osu_beatmapsets SET last_update = UTC_TIMESTAMP() - INTERVAL 40 DAY WHERE beatmapset_id = {Beatmap.CustomIdFloor}");

        var sets = new OsuWebCustomSets(NewOsuWebContext());
        Assert.Equal([(uint)Beatmap.CustomIdFloor], await sets.GraveyardAsync(DateTime.UtcNow.AddDays(-28)));
        Assert.Equal(-2L, await ScalarAsync<long>($"SELECT approved FROM osu_beatmapsets WHERE beatmapset_id = {Beatmap.CustomIdFloor}"));
        Assert.Equal(-2L, await ScalarAsync<long>($"SELECT approved FROM osu_beatmaps WHERE beatmap_id = {Beatmap.CustomIdFloor + 1}"));
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT approved FROM osu_beatmapsets WHERE beatmapset_id = {Beatmap.CustomIdFloor + 1}"));

        // The mapper updates it: back to pending.
        await sets.SetUnrankedStatusAsync(Beatmap.CustomIdFloor, 0);
        Assert.Equal(0L, await ScalarAsync<long>($"SELECT approved FROM osu_beatmapsets WHERE beatmapset_id = {Beatmap.CustomIdFloor}"));
        Assert.Empty(await sets.GraveyardAsync(DateTime.UtcNow.AddDays(-28)));
    }

    private const string Data = """{"mods":[],"statistics":{"great":300}}""";

    private async Task<(ServiceProvider Services, int Player)> SetUpAsync()
    {
        await NewTomYumContextAsync();
        var player = (await new OsuWebUserRepository(NewOsuWebContext()).AddAsync(new User
        {
            Username = "Player", SafeName = "player", Email = "p@x.dev", PasswordBcrypt = _hasher.HashPassword(_hasher.ComputeMd5Hex("pw")), Country = "nl",
        })).Id;
        await new OsuWebBeatmapMirror(NewOsuWebContext()).WriteAsync(new Beatmap
        {
            Id = 100, SetId = 10, Md5 = "md5-100", Status = RankedStatus.Ranked, OsuApiStatus = 1, Version = "Hard", Artist = "Artist", Title = "Song", Creator = "Mapper",
        });
        var services = new ServiceCollection()
            .AddScoped<IUserRepository>(_ => new OsuWebUserRepository(NewOsuWebContext()))
            .AddSingleton<IPasswordHasher>(_hasher)
            .AddScoped(_ => new OsuWebStableClient(NewOsuWebContext()))
            .BuildServiceProvider();
        return (services, player);
    }

    private static DefaultHttpContext Request(ServiceProvider services, string query) =>
        new() { RequestServices = services, Request = { QueryString = new QueryString("?" + query) } };
}
