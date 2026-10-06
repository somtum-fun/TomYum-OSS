using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Performance;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Custom (uploaded) beatmap sets: reading <c>.osz</c> files, storing them, and serving them.</summary>
public sealed class CustomBeatmapTests : OsuWebDatabaseTest, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tomyum-custom-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void OsuFiles_AreEditedInPlace_KeepingEverythingElse()
    {
        var original = Encoding.UTF8.GetBytes(Osu("Hard", 0).Replace("\n", "\r\n", StringComparison.Ordinal));
        var file = OsuFileText.Parse(original);

        Assert.True(file.IsBeatmap);
        Assert.Equal("Hard", file.Get("Metadata", "Version"));
        Assert.Equal(9.0, file.GetDouble("Difficulty", "ApproachRate"));
        Assert.Equal("bg.png", file.Background);
        Assert.Equal(2000, file.BreakMs);
        Assert.Equal(120.0, file.Bpm);
        var objects = file.HitObjects;
        Assert.Equal((20, 1, 1, 1000), (objects.Circles, objects.Sliders, objects.Spinners, objects.FirstMs));
        Assert.Equal(12000, objects.LastMs); // the spinner's end

        // Unchanged, it's the same bytes (the client knows maps by their MD5).
        Assert.Equal(original, file.ToBytes());

        file.Set("Metadata", "BeatmapID", "10000001");
        file.Set("Metadata", "Missing", "added");
        file.Set("Brand New", "Key", "value");
        var edited = Encoding.UTF8.GetString(file.ToBytes());
        Assert.Contains("BeatmapID:10000001\r\n", edited, StringComparison.Ordinal);
        // A new field goes after the section's last one, not after the blank line ending it.
        Assert.Contains("BeatmapSetID:-1\r\nMissing:added\r\n\r\n[Difficulty]", edited, StringComparison.Ordinal);
        Assert.EndsWith("[Brand New]\r\nKey:value", edited, StringComparison.Ordinal);
        Assert.Contains("[HitObjects]\r\n64,192,1000,1,0,0:0:0:0:", edited, StringComparison.Ordinal);
    }

    [Fact]
    public void UploadTickets_AreForOneUserAndSet_AndExpire()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var expires = now.ToUnixTimeSeconds() + 600;
        var ticket = UploadTicket.Compute("secret", 5, null, expires);

        Assert.True(UploadTicket.IsValid("secret", 5, null, expires, ticket, now));
        Assert.True(UploadTicket.IsValid("secret", 5, null, expires, ticket.ToUpperInvariant(), now));
        Assert.False(UploadTicket.IsValid("secret", 6, null, expires, ticket, now));
        Assert.False(UploadTicket.IsValid("secret", 5, 10000000, expires, ticket, now));
        Assert.False(UploadTicket.IsValid("other", 5, null, expires, ticket, now));
        Assert.False(UploadTicket.IsValid("secret", 5, null, expires, ticket, now.AddSeconds(601)));
        Assert.False(UploadTicket.IsValid("secret", 5, null, expires, null, now));
    }

    [MySqlFact]
    public async Task AnUpload_BecomesACustomSet_EverywhereItsNeeded()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var mapper = await users.AddAsync(NewUser("Mapper"));
        var uploader = await users.AddAsync(NewUser("Uploader"));
        var (importer, repository, store, files, resolver) = NewImporter(tomyum, users);

        var result = await importer.ImportAsync(Osz(("Normal", 0), ("Hard", 0), ("4K", 3)), uploader.Id);

        Assert.Equal(Beatmap.CustomIdFloor, result.SetId);
        Assert.False(result.Updated);
        Assert.Equal(mapper.Id, result.OwnerId); // the file's creator has an account
        Assert.Equal(("Artist", "Song", "Mapper"), (result.Artist, result.Title, result.Creator));
        Assert.Equal([Beatmap.CustomIdFloor, Beatmap.CustomIdFloor + 1, Beatmap.CustomIdFloor + 2], result.Difficulties.Select(d => d.BeatmapId));
        Assert.All(result.Difficulties, d => Assert.True(d.StarRating > 0));

        foreach (var difficulty in result.Difficulties)
        {
            // The file says which map it is, and the stored checksum is that file's.
            var bytes = await files.GetAsync(difficulty.BeatmapId);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.Contains(Invariant($"BeatmapID:{difficulty.BeatmapId}\n"), text, StringComparison.Ordinal);
            Assert.Contains(Invariant($"BeatmapSetID:{result.SetId}\n"), text, StringComparison.Ordinal);
            Assert.Equal(OsuFileText.Md5(bytes), difficulty.Md5);

            var stored = await repository.GetByIdAsync(difficulty.BeatmapId);
            Assert.Equal((difficulty.Md5, RankedStatus.Pending, result.SetId), (stored!.Md5, stored.Status, stored.SetId));
            Assert.True(stored.MaxCombo > 0);
            Assert.Equal(difficulty.Md5, (await resolver.GetByMd5Async(difficulty.Md5))!.Md5);
        }
        Assert.Equal([0, 0, 3], (await repository.GetBySetAsync(result.SetId)).Select(b => b.Mode).Order());
        // The owner and ids are kept, so the mapper can update it from the game later.
        var sets = new CustomBeatmapSetRepository(NewTomYumContext());
        Assert.Equal(mapper.Id, (await sets.GetAsync(result.SetId))!.OwnerId);
        Assert.Equal(result.Difficulties.Select(d => d.BeatmapId), await sets.GetSlotsAsync(result.SetId));

        // The download is the uploaded set with the rewritten difficulties.
        using (var download = ZipFile.OpenRead(store.OszPath(result.SetId)))
        {
            Assert.Equal(["4K.osu", "Hard.osu", "Normal.osu", "audio.mp3", "bg.png"], download.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
            using var reader = new StreamReader(download.GetEntry("Hard.osu")!.Open());
            Assert.Contains(Invariant($"BeatmapID:{Beatmap.CustomIdFloor + 1}"), await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }

        // The website: the mapper owns it, it has a cover and the unicode names.
        Assert.Equal((long)mapper.Id, await ScalarAsync<long>(Invariant($"SELECT user_id FROM osu_beatmapsets WHERE beatmapset_id = {result.SetId}")));
        Assert.Equal(1L, await ScalarAsync<long>(Invariant($"SELECT cover_updated_at IS NOT NULL FROM osu_beatmapsets WHERE beatmapset_id = {result.SetId}")));
        Assert.Equal("ソング", await ScalarAsync<string>(Invariant($"SELECT title_unicode FROM osu_beatmapsets WHERE beatmapset_id = {result.SetId}")));
        Assert.Equal(3L, await ScalarAsync<long>(Invariant($"SELECT COUNT(*) FROM osu_beatmaps WHERE beatmapset_id = {result.SetId} AND user_id = {mapper.Id} AND deleted_at IS NULL")));
        Assert.Equal(9.0, await ScalarAsync<double>(Invariant($"SELECT diff_approach FROM osu_beatmaps WHERE beatmap_id = {Beatmap.CustomIdFloor + 1}")));
        Assert.Equal(20L, await ScalarAsync<long>(Invariant($"SELECT countNormal FROM osu_beatmaps WHERE beatmap_id = {Beatmap.CustomIdFloor + 1}")));

        // Covers and thumbnails in every size, from the background.
        foreach (var (size, (width, height)) in CustomBeatmapStore.CoverSizes)
        {
            using var cover = SKBitmap.Decode(store.CoverPath(result.SetId, size));
            Assert.Equal((width, height), (cover.Width, cover.Height));
        }
        Assert.Null(store.CoverPath(result.SetId, "../../secret"));
        Assert.NotNull(store.PreviewPath(result.SetId));
    }

    [MySqlFact]
    public async Task GuestDifficulties_BelongToTheirMappers_AndHandSetOwnersStay()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var mapper = await users.AddAsync(NewUser("Mapper"));
        var guest = await users.AddAsync(NewUser("Guest"));
        var other = await users.AddAsync(NewUser("Other"));
        var (importer, _, _, _, _) = NewImporter(tomyum, users);
        byte[] Package() => Zip(
            ("Normal.osu", Encoding.UTF8.GetBytes(Osu("Normal", 0))),
            ("Guest's Insane.osu", Encoding.UTF8.GetBytes(Osu("Guest's Insane", 0, 50, creator: "Guest"))),
            ("Hard.osu", Encoding.UTF8.GetBytes(Osu("Hard", 0, 100))),
            ("bg.png", Png()), ("audio.mp3", new byte[] { 0xFF, 0xFB, 0x90, 0x00 }));

        var result = await importer.ImportAsync(Package(), mapper.Id);
        var insane = result.Difficulties.Single(d => d.Version == "Guest's Insane").BeatmapId;
        var hard = result.Difficulties.Single(d => d.Version == "Hard").BeatmapId;
        Assert.Equal(mapper.Id, result.OwnerId);
        Assert.Equal((long)guest.Id, await ScalarAsync<long>($"SELECT user_id FROM beatmap_owners WHERE beatmap_id = {insane}"));
        Assert.Equal((long)guest.Id, await ScalarAsync<long>($"SELECT user_id FROM osu_beatmaps WHERE beatmap_id = {insane}"));
        Assert.Equal((long)mapper.Id, await ScalarAsync<long>($"SELECT user_id FROM beatmap_owners WHERE beatmap_id = {hard}"));

        // The mapper gives Hard to someone on the website; an update of the set keeps that.
        await ExecuteAsync($"UPDATE beatmap_owners SET user_id = {other.Id} WHERE beatmap_id = {hard}");
        await importer.ImportAsync(Package(), mapper.Id, updateSetId: result.SetId);
        Assert.Equal((long)other.Id, await ScalarAsync<long>($"SELECT user_id FROM beatmap_owners WHERE beatmap_id = {hard}"));
        Assert.Equal((long)other.Id, await ScalarAsync<long>($"SELECT user_id FROM osu_beatmaps WHERE beatmap_id = {hard}"));
        Assert.Equal((long)guest.Id, await ScalarAsync<long>($"SELECT user_id FROM beatmap_owners WHERE beatmap_id = {insane}"));
    }

    [MySqlFact]
    public async Task AnUpdate_KeepsTheIdsOfDifficultiesItKeeps()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var uploader = await users.AddAsync(NewUser("Uploader"));
        var (importer, repository, _, files, _) = NewImporter(tomyum, users);
        var first = await importer.ImportAsync(Osz(("Normal", 0), ("Hard", 0)), uploader.Id);
        Assert.Equal(uploader.Id, first.OwnerId); // nobody is called Mapper here

        var update = await importer.ImportAsync(Osz(("Hard", 0, 30), ("Insane", 0, 40)), uploader.Id, first.SetId);

        Assert.True(update.Updated);
        Assert.Equal(first.SetId, update.SetId);
        var hard = update.Difficulties.Single(d => d.Version == "Hard");
        Assert.Equal(first.Difficulties.Single(d => d.Version == "Hard").BeatmapId, hard.BeatmapId);
        Assert.Equal(Beatmap.CustomIdFloor + 2, update.Difficulties.Single(d => d.Version == "Insane").BeatmapId);
        Assert.NotEqual(first.Difficulties.Single(d => d.Version == "Hard").Md5, hard.Md5);
        Assert.Equal(hard.Md5, OsuFileText.Md5(await files.GetAsync(hard.BeatmapId)));
        Assert.Equal(hard.Md5, (await repository.GetByIdAsync(hard.BeatmapId))!.Md5);

        // The dropped difficulty is gone from the website.
        var normal = first.Difficulties.Single(d => d.Version == "Normal").BeatmapId;
        Assert.Equal(1L, await ScalarAsync<long>(Invariant($"SELECT deleted_at IS NOT NULL FROM osu_beatmaps WHERE beatmap_id = {normal}")));
        Assert.Equal(2L, await ScalarAsync<long>(Invariant($"SELECT COUNT(*) FROM osu_beatmaps WHERE beatmapset_id = {first.SetId} AND deleted_at IS NULL")));

        // Ranked sets aren't changed under their players.
        await repository.SetStatusAsync(hard.BeatmapId, RankedStatus.Ranked);
        var refused = await Assert.ThrowsAsync<CustomUploadException>(() => importer.ImportAsync(Osz(("Hard", 0, 50)), uploader.Id, first.SetId));
        Assert.Contains("ranked", refused.Message, StringComparison.Ordinal);
    }

    [MySqlFact]
    public async Task BadUploads_AreRefused_WithAReason()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var uploader = await users.AddAsync(NewUser("Uploader"));
        var (importer, _, store, _, _) = NewImporter(tomyum, users);

        async Task<string> Refused(byte[] osz, int? set = null) =>
            (await Assert.ThrowsAsync<CustomUploadException>(() => importer.ImportAsync(osz, uploader.Id, set))).Message;

        Assert.Equal("That isn't an .osz file.", await Refused(Encoding.UTF8.GetBytes("not a zip")));
        Assert.Contains("no difficulties", await Refused(Zip(("audio.mp3", [1, 2, 3]))), StringComparison.Ordinal);
        Assert.Contains("isn't an osu! beatmap", await Refused(Zip(("x.osu", Encoding.UTF8.GetBytes("hello"))) ), StringComparison.Ordinal);
        Assert.Contains("same artist and title", await Refused(Zip(
            ("a.osu", Encoding.UTF8.GetBytes(Osu("A", 0))),
            ("b.osu", Encoding.UTF8.GetBytes(Osu("B", 0).Replace("Title:Song", "Title:Other", StringComparison.Ordinal))))), StringComparison.Ordinal);
        Assert.Contains("Two difficulties", await Refused(Zip(
            ("a.osu", Encoding.UTF8.GetBytes(Osu("Hard", 0))),
            ("b.osu", Encoding.UTF8.GetBytes(Osu("hard", 0, 30))))), StringComparison.Ordinal);
        Assert.Contains("Only custom sets", await Refused(Osz(("Hard", 0)), 123), StringComparison.Ordinal);

        // A set downloaded from here, uploaded again as a new one.
        var stored = await importer.ImportAsync(Osz(("Hard", 0)), uploader.Id);
        var again = await File.ReadAllBytesAsync(store.OszPath(stored.SetId));
        Assert.Contains("already on the server", await Refused(again), StringComparison.Ordinal);
    }

    [MySqlFact]
    public async Task CustomSets_AreServedToTheClient()
    {
        await using var tomyum = await NewTomYumContextAsync();
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var uploader = await users.AddAsync(NewUser("Uploader"));
        var (importer, _, store, files, _) = NewImporter(tomyum, users);
        var set = await importer.ImportAsync(Osz(("Normal", 0), ("Hard", 0)), uploader.Id);

        var scopes = new ServiceCollection().AddScoped(_ => NewTomYumContext()).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var catalog = new CustomBeatmapCatalog(scopes, files);
        var options = Options.Create(new DirectOptions());

        // Download, thumbnails, preview, covers: this server's files.
        var download = Assert.IsType<PhysicalFileHttpResult>(DirectEndpoints.HandleDownload(set.SetId, new DefaultHttpContext().Request, options, store));
        Assert.Equal(store.OszPath(set.SetId), download.FileName);
        Assert.IsType<NotFound>(DirectEndpoints.HandleDownload(set.SetId + 1, new DefaultHttpContext().Request, options, store));
        Assert.IsType<RedirectHttpResult>(DirectEndpoints.HandleDownload(123, new DefaultHttpContext().Request, options, store));

        using var previews = new AudioPreviews(new ConfigurationBuilder().Build(), new NoHttp(), scopes, NullLogger<AudioPreviews>.Instance);
        Task<IResult> Asset(string path) => DirectEndpoints.HandleBeatmapAsset(Request(path).HttpContext, options, store, previews, CancellationToken.None);
        Assert.Equal("image/jpeg", Assert.IsType<PhysicalFileHttpResult>(await Asset(Invariant($"/thumb/{set.SetId}l.jpg"))).ContentType);
        Assert.Equal("audio/mpeg", Assert.IsType<PhysicalFileHttpResult>(await Asset(Invariant($"/preview/{set.SetId}.mp3"))).ContentType);
        Assert.IsType<RedirectHttpResult>(await Asset("/thumb/123l.jpg"));
        Assert.IsType<PhysicalFileHttpResult>(DirectEndpoints.HandleCover(set.SetId, "card@2x.jpg", store));
        Assert.Equal("image/png", Assert.IsType<PhysicalFileHttpResult>(DirectEndpoints.HandleCover(set.SetId, "raw.jpg", store)).ContentType);
        Assert.IsType<NotFound>(DirectEndpoints.HandleCover(set.SetId, "nope.jpg", store));
        Assert.IsType<NotFound>(DirectEndpoints.HandleCover(123, "cover.jpg", store));

        // osu!direct: search and set info.
        var found = Assert.Single(await catalog.SearchAsync("artist song", null, null, 100));
        Assert.Equal(set.SetId, found.SetId);
        Assert.Empty(await catalog.SearchAsync("nothing like it", null, null, 100));
        Assert.Empty(await catalog.SearchAsync(null, 3, null, 100));
        Assert.Empty(await catalog.SearchAsync(null, null, 1, 100));

        // Imported osu! sets: only with includeImported (numbered builds' osu!direct).
        tomyum.Beatmaps.Add(new Beatmap { Id = 4242, SetId = 4240, Md5 = new string('4', 32), Artist = "Imported", Title = "Tune", Version = "Easy", Creator = "Someone", Status = RankedStatus.Ranked });
        await tomyum.SaveChangesAsync();
        Assert.Empty(await catalog.SearchAsync("imported tune", null, null, 100));
        Assert.Equal(4240, Assert.Single(await catalog.SearchAsync("imported tune", null, null, 100, includeImported: true)).SetId);

        var line = catalog.ToDirectLine(found);
        Assert.StartsWith(Invariant($"{set.SetId}.osz|Artist|Song|Mapper|0|10.0|"), line, StringComparison.Ordinal);
        Assert.Contains("⭐] Hard {cs: 4 / od: 8 / ar: 9 / hp: 5}@0", line, StringComparison.Ordinal);

        var info = Assert.IsType<ContentHttpResult>(await DirectEndpoints.HandleSearchSetAsync(
            Context(Invariant($"b={set.Difficulties[1].BeatmapId}")), new NoHttp(), options, catalog, new PlayerRegistry(), CancellationToken.None));
        Assert.StartsWith(Invariant($"{set.SetId}.osz|Artist|Song|Mapper|0|"), info.ResponseContent, StringComparison.Ordinal);
        var byChecksum = Assert.IsType<ContentHttpResult>(await DirectEndpoints.HandleSearchSetAsync(
            Context("c=" + set.Difficulties[0].Md5), new NoHttp(), options, catalog, new PlayerRegistry(), CancellationToken.None));
        Assert.Equal(info.ResponseContent, byChecksum.ResponseContent);
        var missing = Assert.IsType<ContentHttpResult>(await DirectEndpoints.HandleSearchSetAsync(
            Context(Invariant($"s={set.SetId + 5}")), new NoHttp(), options, catalog, new PlayerRegistry(), CancellationToken.None));
        Assert.Equal(string.Empty, missing.ResponseContent);

        // Listed first even when the mirror is down.
        var search = Assert.IsType<ContentHttpResult>(await DirectEndpoints.HandleSearchAsync(
            Context("q=Song&r=4&m=-1&p=0"), new NoHttp(), options, catalog, new PlayerRegistry(), CancellationToken.None));
        Assert.StartsWith(Invariant($"1\n{set.SetId}.osz|Artist|Song"), search.ResponseContent!.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [MySqlFact]
    public async Task OsuSetsStaffGaveAStatus_ShowItInOsuDirect()
    {
        await using var tomyum = await NewTomYumContextAsync();
        // Ranked here (graveyard on osu!), and ranked on osu! but unranked here.
        tomyum.Beatmaps.Add(new Beatmap { Id = 971401, SetId = 971400, Md5 = new string('a', 32), Artist = "Some Artist", Title = "MariannE", Version = "Insane", Creator = "Mapper", Status = RankedStatus.Ranked, StatusFrozen = true });
        tomyum.Beatmaps.Add(new Beatmap { Id = 971402, SetId = 971400, Md5 = new string('b', 32), Artist = "Some Artist", Title = "MariannE", Version = "Hard", Creator = "Mapper", Status = RankedStatus.Pending });
        tomyum.Beatmaps.Add(new Beatmap { Id = 556, SetId = 555, Md5 = new string('c', 32), Artist = "Other", Title = "Unranked Here", Version = "Easy", Creator = "Mapper", Status = RankedStatus.Pending, StatusFrozen = true });
        await tomyum.SaveChangesAsync();

        var scopes = new ServiceCollection().AddScoped(_ => NewTomYumContext()).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var catalog = new CustomBeatmapCatalog(scopes);
        var options = Options.Create(new DirectOptions());
        var mirror = new Mirror(
            """[{"SetID":971400,"Artist":"Some Artist","Title":"MariannE","Creator":"Mapper","RankedStatus":-2,"LastUpdate":"2020-01-01","ChildrenBeatmaps":[{"DiffName":"Insane","Mode":0}]},""" +
            """{"SetID":555,"Artist":"Other","Title":"Unranked Here","Creator":"Mapper","RankedStatus":1,"LastUpdate":"2020-01-01","ChildrenBeatmaps":[{"DiffName":"Easy","Mode":0}]}]""",
            """{"SetID":971400,"Artist":"Some Artist","Title":"MariannE","Creator":"Mapper","RankedStatus":-2,"LastUpdate":"2020-01-01"}""");

        async Task<string> Search(string query) => Assert.IsType<ContentHttpResult>(await DirectEndpoints.HandleSearchAsync(
            Context("u=player&" + query), mirror, options, catalog, new PlayerRegistry(), CancellationToken.None)).ResponseContent!.ReplaceLineEndings("\n");

        // Everything: the mirror's sets with this server's statuses.
        var all = await Search("q=&r=4&m=-1&p=0");
        Assert.Contains("971400.osz|Some Artist|MariannE|Mapper|1|", all, StringComparison.Ordinal);
        Assert.Contains("555.osz|Other|Unranked Here|Mapper|0|", all, StringComparison.Ordinal);

        // Ranked: the set ranked here (once), not the one unranked here.
        var ranked = await Search("q=&r=0&m=-1&p=0");
        Assert.Single(ranked.Split('\n'), l => l.StartsWith("971400.osz|", StringComparison.Ordinal));
        Assert.DoesNotContain("555.osz", ranked, StringComparison.Ordinal);

        // Set info.
        var info = Assert.IsType<ContentHttpResult>(await DirectEndpoints.HandleSearchSetAsync(
            Context("s=971400"), mirror, options, catalog, new PlayerRegistry(), CancellationToken.None));
        Assert.StartsWith("971400.osz|Some Artist|MariannE|Mapper|1|", info.ResponseContent, StringComparison.Ordinal);
    }

    private (CustomBeatmapImporter Importer, IBeatmapRepository Repository, CustomBeatmapStore Store, OsuFileCache Files, BeatmapService Resolver) NewImporter(TomYumDbContext tomyum, IUserRepository users)
    {
        var osuWeb = NewOsuWebContext();
        var repository = new OsuWebMirroringBeatmapRepository(new BeatmapRepository(tomyum), new OsuWebBeatmapMirror(osuWeb), osuWeb);
        var store = new CustomBeatmapStore(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [CustomBeatmapStore.PathKey] = Path.Combine(_directory, "beatmaps") })
            .Build());
        var files = new OsuFileCache(Path.Combine(_directory, "osu"), new HttpClient(new NoHttp()), NullLogger<OsuFileCache>.Instance);
        var calculator = new OfficialPerformanceCalculator(files, new MemoryCache(new MemoryCacheOptions()), NullLogger<OfficialPerformanceCalculator>.Instance);
        var resolver = new BeatmapService(new NoMapsApi());
        var importer = new CustomBeatmapImporter(repository, users, store, files, TimeProvider.System, NullLogger<CustomBeatmapImporter>.Instance,
            calculator, website: new OsuWebCustomSets(NewOsuWebContext()), resolver: resolver, sets: new CustomBeatmapSetRepository(NewTomYumContext()));
        return (importer, repository, store, files, resolver);
    }

    private static HttpRequest Request(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        return context.Request;
    }

    private static DefaultHttpContext Context(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?" + query);
        return context;
    }

    /// <summary>An <c>.osz</c> with the given difficulties (version, mode, and where the objects start), a background and audio.</summary>
    private static byte[] Osz(params (string Version, int Mode, int Start)[] difficulties) =>
        Zip([.. difficulties.Select(d => (d.Version + ".osu", Encoding.UTF8.GetBytes(Osu(d.Version, d.Mode, d.Start)))),
             ("bg.png", Png()), ("audio.mp3", new byte[] { 0xFF, 0xFB, 0x90, 0x00 })]);

    private static byte[] Osz(params (string Version, int Mode)[] difficulties) =>
        Osz(difficulties.Select(d => (d.Version, d.Mode, 0)).ToArray());

    private static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }
        return output.ToArray();
    }

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(1920, 1080);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(new SKColor(40, 120, 200));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A small but real map: 20 circles, a slider and a spinner (their times shifted by <paramref name="shift"/> ms).</summary>
    private static string Osu(string version, int mode, int shift = 0, string creator = "Mapper")
    {
        var objects = new StringBuilder();
        for (var i = 0; i < 20; i++)
            objects.Append(Invariant($"{64 + (i % 4) * 128},192,{1000 + shift + i * 250},1,0,0:0:0:0:\n"));
        objects.Append(Invariant($"100,100,{7000 + shift},2,0,B|300:100,1,200\n"));
        objects.Append(Invariant($"256,192,{8000 + shift},12,0,{12000 + shift},0:0:0:0:"));
        return $"""
            osu file format v14

            [General]
            AudioFilename: audio.mp3
            PreviewTime: 1000
            Mode: {mode}

            [Metadata]
            Title:Song
            TitleUnicode:ソング
            Artist:Artist
            ArtistUnicode:アーティスト
            Creator:{creator}
            Version:{version}
            Source:
            Tags:test words
            BeatmapID:0
            BeatmapSetID:-1

            [Difficulty]
            HPDrainRate:5
            CircleSize:4
            OverallDifficulty:8
            ApproachRate:9
            SliderMultiplier:1.4
            SliderTickRate:1

            [Events]
            //Background and Video events
            0,0,"bg.png",0,0
            //Break Periods
            2,4000,6000

            [TimingPoints]
            0,500,4,2,0,50,1,0

            [HitObjects]
            {objects}
            """.ReplaceLineEndings("\n");
    }

    private static User NewUser(string name) => new()
    {
        Username = name,
        SafeName = User.MakeSafeName(name),
        Email = $"{name.ToLowerInvariant()}@x.dev",
        PasswordBcrypt = "x",
        Country = "nl",
    };

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>No network: the mirror (and osu.ppy.sh) are down.</summary>
    private sealed class NoHttp : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }

    /// <summary>The mirror: <paramref name="search"/> for searches, <paramref name="set"/> for set info.</summary>
    private sealed class Mirror(string search, string set) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.Query.Contains("amount=", StringComparison.Ordinal) ? search : set),
            });
    }

    private sealed class NoMapsApi : IOsuApiClient
    {
        public bool SupportsSince => false;

        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);
    }
}
