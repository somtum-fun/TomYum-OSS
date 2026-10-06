using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Performance;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Persistence.Repositories;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using TomYum.Server.Beatmaps.Osz2;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>Beatmap submission from the game clients: the stable editor's .osz2 and lazer's submission service.</summary>
public sealed class SubmissionTests : OsuWebDatabaseTest, IDisposable
{
    private const string PasswordMd5 = "5f4dcc3b5aa765d61d8327deb882cf99";

    private static readonly string[] AllScopes = ["*"];

    private static readonly BcryptPasswordHasher Passwords = new();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tomyum-submit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stable-submission.osz2"));

    [Fact]
    public void Osz2Packages_AreDecrypted_AndChecked()
    {
        var package = Osz2Package.Read(Fixture());

        Assert.Equal(("Artist", "Song", "Mapper", "10000000"),
            (package.Get(Osz2Meta.Artist), package.Get(Osz2Meta.Title), package.Get(Osz2Meta.Creator), package.Get(Osz2Meta.BeatmapSetId)));
        Assert.Equal(10000000, package.BeatmapIds["Artist - Song (Mapper) [Normal].osu"]);
        Assert.Equal(10000001, package.BeatmapIds["Artist - Song (Mapper) [Hard].osu"]);
        Assert.Equal(["Artist - Song (Mapper) [Hard].osu", "Artist - Song (Mapper) [Normal].osu", "audio.mp3", "bg.png"],
            package.Files.Select(f => f.Name).Order(StringComparer.Ordinal));
        var hard = Encoding.UTF8.GetString(package.Files.Single(f => f.Name.EndsWith("[Hard].osu", StringComparison.Ordinal)).Content);
        Assert.StartsWith("osu file format v14\r\n", hard, StringComparison.Ordinal);
        Assert.Contains("BeatmapID:10000001\r\n", hard, StringComparison.Ordinal);

        using var osz = new ZipArchive(new MemoryStream(package.ToOsz()));
        Assert.Equal(4, osz.Entries.Count);

        // Any damage shows: the metadata or file table hash, the metadata, or a file's content.
        foreach (var at in new[] { 25, 45, 120, Fixture().Length - 20 })
        {
            var damaged = Fixture();
            damaged[at] ^= 0x55;
            Assert.Throws<InvalidDataException>(() => Osz2Package.Read(damaged));
        }
        Assert.Throws<InvalidDataException>(() => Osz2Package.Read(Encoding.UTF8.GetBytes("PK not an osz2 at all")));
    }

    [MySqlFact]
    public async Task TheStableEditor_SubmitsASet()
    {
        var (submissions, services, sets) = await SetUpAsync();
        var mapper = await AddUserAsync("Mapper");

        // getid: two new difficulties (the editor sends -1 for maps without an id).
        var getId = await Text(SubmissionEndpoints.StableGetIdAsync(Stable(services, $"u=Mapper&h={PasswordMd5}&s=-1&b=-1,-1&z="), submissions, services.GetRequiredService<IBeatmapRepository>(), default));
        Assert.Equal("0\n10000000\n10000000,10000001\n1\n3\n0", getId);
        Assert.Equal("5", (await Text(SubmissionEndpoints.StableGetIdAsync(Stable(services, "u=Mapper&h=wrong&s=-1&b=-1"), submissions, services.GetRequiredService<IBeatmapRepository>(), default)))[..1]);
        // An osu! set belongs to its mapper there.
        Assert.StartsWith("1\n", await Text(SubmissionEndpoints.StableGetIdAsync(Stable(services, $"u=Mapper&h={PasswordMd5}&s=39804&b=126645"), submissions, services.GetRequiredService<IBeatmapRepository>(), default)), StringComparison.Ordinal);

        // upload: the package the editor made with those ids.
        var osz2 = Fixture();
        var upload = await Text(SubmissionEndpoints.StableUploadAsync(StableUpload(services, osz2, $"u=Mapper&h={PasswordMd5}&s=10000000&t=1&z={Md5(osz2)}"), submissions, default));
        Assert.Equal("0", upload);
        Assert.StartsWith("5\n", await Text(SubmissionEndpoints.StableUploadAsync(StableUpload(services, osz2, $"u=Mapper&h={PasswordMd5}&s=10000000&t=1&z={new string('0', 32)}"), submissions, default)), StringComparison.Ordinal);

        // The files are kept exactly as the editor has them (it knows its maps by their checksums).
        var package = Osz2Package.Read(osz2);
        var repository = services.GetRequiredService<IBeatmapRepository>();
        foreach (var (name, id) in package.BeatmapIds)
        {
            var stored = await repository.GetByIdAsync(id);
            Assert.Equal(Md5(package.Files.Single(f => f.Name == name).Content), stored!.Md5);
            Assert.Equal(RankedStatus.Pending, stored.Status);
        }
        var record = await sets.GetAsync(10000000);
        Assert.Equal(mapper.Id, record!.OwnerId);
        Assert.NotNull(record.UploadedAt);
        Assert.Equal((long)mapper.Id, await ScalarAsync<long>("SELECT user_id FROM osu_beatmapsets WHERE beatmapset_id = 10000000"));

        // post: not finished yet → a work in progress on the website.
        var post = await SubmissionEndpoints.StablePostAsync(StablePost(services, "Mapper", "10000000", complete: false), submissions, default);
        Assert.IsType<ContentHttpResult>(post);
        Assert.Equal(-1L, await ScalarAsync<long>("SELECT approved FROM osu_beatmapsets WHERE beatmapset_id = 10000000"));
        Assert.Equal(2L, await ScalarAsync<long>("SELECT COUNT(*) FROM osu_beatmaps WHERE beatmapset_id = 10000000 AND approved = -1"));
        // The login also works the way getid and upload send it (u and h in the query).
        Assert.IsType<ContentHttpResult>(await SubmissionEndpoints.StablePostAsync(StablePost(services, "Mapper", "10000000", complete: true, loginInQuery: true), submissions, default));
        Assert.Equal(0L, await ScalarAsync<long>("SELECT approved FROM osu_beatmapsets WHERE beatmapset_id = 10000000"));
        Assert.Equal(CustomBeatmapSet.Pending, (await sets.GetAsync(10000000))!.Target);

        // Coming back to it: the same ids (the editor now sends them), nothing new.
        Assert.Equal("0\n10000000\n10000000,10000001\n1\n3\n0",
            await Text(SubmissionEndpoints.StableGetIdAsync(Stable(services, $"u=Mapper&h={PasswordMd5}&s=10000000&b=10000000,10000001"), submissions, repository, default)));
        // Someone else can't.
        await AddUserAsync("Other");
        Assert.StartsWith("1\n", await Text(SubmissionEndpoints.StableGetIdAsync(Stable(services, $"u=Other&h={PasswordMd5}&s=10000000&b=10000000"), submissions, repository, default)), StringComparison.Ordinal);
        Assert.IsType<StatusCodeHttpResult>(await SubmissionEndpoints.StablePostAsync(StablePost(services, "Other", "10000000", complete: true), submissions, default));
        // Once ranked, nobody can.
        await repository.SetStatusAsync(10000000, RankedStatus.Ranked);
        Assert.StartsWith("3\n", await Text(SubmissionEndpoints.StableGetIdAsync(Stable(services, $"u=Mapper&h={PasswordMd5}&s=10000000&b=10000000,10000001"), submissions, repository, default)), StringComparison.Ordinal);
    }

    [MySqlFact]
    public async Task Lazer_CreatesUpdatesAndPatchesASet()
    {
        var (submissions, services, sets) = await SetUpAsync();
        var mapper = await AddUserAsync("Mapper");
        var token = await SignInAsync(services, mapper.Id);

        // Create: two ids, no files yet.
        var created = await Json(await SubmissionEndpoints.LazerPutAsync(Lazer(services, token), new LazerPutBeatmapSet(null, 2, null, "Pending", false), submissions, default));
        Assert.Contains("\"beatmapset_id\":10000000", created, StringComparison.Ordinal);
        Assert.Contains("\"beatmap_ids\":[10000000,10000001]", created, StringComparison.Ordinal);
        Assert.Contains("\"files\":[]", created, StringComparison.Ordinal);

        // Full upload of what lazer exported with those ids.
        var normal = Encoding.UTF8.GetBytes(Osu("Normal", 10000000, 10000000));
        var hard = Encoding.UTF8.GetBytes(Osu("Hard", 10000001, 10000000));
        Assert.IsType<NoContent>(await SubmissionEndpoints.LazerReplaceAsync(10000000, Lazer(services, token, ("beatmapArchive", "x.osz", Zip(("Normal.osu", normal), ("Hard.osu", hard), ("audio.mp3", [1, 2, 3])))), submissions, default));
        var repository = services.GetRequiredService<IBeatmapRepository>();
        Assert.Equal(Md5(hard), (await repository.GetByIdAsync(10000001))!.Md5);

        // Next time lazer sees the files, and patches only what changed.
        var again = await Json(await SubmissionEndpoints.LazerPutAsync(Lazer(services, token), new LazerPutBeatmapSet(10000000, 0, [10000000, 10000001], "Pending", false), submissions, default));
        Assert.Contains($"\"filename\":\"Hard.osu\",\"sha2_hash\":\"{Convert.ToHexString(SHA256.HashData(hard))}\"", again, StringComparison.Ordinal);
        var newHard = Encoding.UTF8.GetBytes(Osu("Hard", 10000001, 10000000, shift: 40));
        Assert.IsType<NoContent>(await SubmissionEndpoints.LazerPatchAsync(10000000, Lazer(services, token, ("filesChanged", "Hard.osu", newHard)), submissions, default));
        Assert.Equal(Md5(newHard), (await repository.GetByIdAsync(10000001))!.Md5);
        Assert.Equal(Md5(normal), (await repository.GetByIdAsync(10000000))!.Md5);

        // Dropping a difficulty: it goes from the set, the website, and play.
        var dropped = await Json(await SubmissionEndpoints.LazerPutAsync(Lazer(services, token), new LazerPutBeatmapSet(10000000, 0, [10000000], "WIP", false), submissions, default));
        Assert.Contains("\"beatmap_ids\":[10000000]", dropped, StringComparison.Ordinal);
        var incomplete = await SubmissionEndpoints.LazerPatchAsync(10000000, Lazer(services, token), submissions, default);
        Assert.Equal(422, StatusOf(incomplete)); // Hard.osu is still in the package
        Assert.IsType<NoContent>(await SubmissionEndpoints.LazerPatchAsync(10000000, Lazer(services, token, form: ("filesDeleted", "Hard.osu")), submissions, default));
        Assert.Equal(RankedStatus.NotSubmitted, (await repository.GetByIdAsync(10000001))!.Status);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT deleted_at IS NOT NULL FROM osu_beatmaps WHERE beatmap_id = 10000001"));
        Assert.Equal(-1L, await ScalarAsync<long>("SELECT approved FROM osu_beatmapsets WHERE beatmapset_id = 10000000")); // submitted as WIP
        Assert.Equal([10000000], await sets.GetSlotsAsync(10000000));

        // The rules.
        Assert.IsType<UnauthorizedHttpResult>(await SubmissionEndpoints.LazerPutAsync(Lazer(services, "not a token"), new LazerPutBeatmapSet(null, 1, null, "Pending", false), submissions, default));
        Assert.Equal(422, StatusOf(await SubmissionEndpoints.LazerPutAsync(Lazer(services, token), new LazerPutBeatmapSet(10000000, 0, [10000001], "Pending", false), submissions, default)));
        Assert.Equal(422, StatusOf(await SubmissionEndpoints.LazerPutAsync(Lazer(services, token), new LazerPutBeatmapSet(null, 0, null, "Pending", false), submissions, default)));
        Assert.Equal(422, StatusOf(await SubmissionEndpoints.LazerPutAsync(Lazer(services, token), new LazerPutBeatmapSet(null, 129, null, "Pending", false), submissions, default)));
        Assert.Equal(403, StatusOf(await SubmissionEndpoints.LazerPutAsync(Lazer(services, token), new LazerPutBeatmapSet(null, 1, null, "Ranked", false), submissions, default)));
        Assert.Equal(403, StatusOf(await SubmissionEndpoints.LazerPutAsync(Lazer(services, token), new LazerPutBeatmapSet(39804, 0, [126645], "Pending", false), submissions, default)));
        Assert.Equal(404, StatusOf(await SubmissionEndpoints.LazerPutAsync(Lazer(services, token), new LazerPutBeatmapSet(10000099, 1, null, "Pending", false), submissions, default)));
        var other = await AddUserAsync("Other");
        Assert.Equal(403, StatusOf(await SubmissionEndpoints.LazerPutAsync(Lazer(services, await SignInAsync(services, other.Id)), new LazerPutBeatmapSet(10000000, 0, [10000000], "Pending", false), submissions, default)));
        // Files for ids the set didn't give out.
        var stray = Encoding.UTF8.GetBytes(Osu("Stray", 12345, 10000000));
        Assert.Equal(422, StatusOf(await SubmissionEndpoints.LazerReplaceAsync(10000000, Lazer(services, token, ("beatmapArchive", "x.osz", Zip(("Normal.osu", normal), ("Stray.osu", stray)))), submissions, default)));
    }

    [MySqlFact]
    public async Task Mappers_HaveAQuota_AndUnusedIdsExpire()
    {
        var (submissions, _, sets) = await SetUpAsync();
        var mapper = await AddUserAsync("Mapper");

        for (var i = 0; i < 4; i++)
            await submissions.ReserveAsync(mapper, null, [null], CustomBeatmapSet.Pending, strictIds: true);
        var full = await Assert.ThrowsAsync<SubmissionException>(() => submissions.ReserveAsync(mapper, null, [null], CustomBeatmapSet.Pending, strictIds: true));
        Assert.Contains("limit", full.Message, StringComparison.Ordinal);

        // Ids nobody uploaded files for stop counting after a day, freeing the slots, but stay taken.
        await ExecuteTomYumAsync("UPDATE custom_beatmapsets SET updated_at = UTC_TIMESTAMP() - INTERVAL 2 DAY");
        var next = await submissions.ReserveAsync(mapper, null, [null, null], CustomBeatmapSet.Pending, strictIds: true);
        Assert.Equal(3, next.Remaining);
        Assert.Equal(10000004, next.SetId);
        Assert.Equal([10000004, 10000005], next.BeatmapIds);
        Assert.Equal(5, (await sets.ListByOwnerAsync(mapper.Id)).Count);

        // Restricted or silenced mappers can't submit.
        var restricted = services(mapper.Id);
        await Assert.ThrowsAsync<SubmissionException>(() => restricted.ReserveAsync(mapper, null, [null], CustomBeatmapSet.Pending, strictIds: true));

        BeatmapSubmissions services(int restrictedId)
        {
            var users = new RestrictedUsers();
            users.Load([restrictedId]);
            return NewSubmissions(users);
        }
    }

    [MySqlFact]
    public async Task LazerLogins_AreChecked()
    {
        var (_, services, _) = await SetUpAsync();
        var mapper = await AddUserAsync("Mapper");
        var tokens = services.GetRequiredService<LazerTokens>();

        var token = await SignInAsync(services, mapper.Id);
        Assert.Equal(mapper.Id, await tokens.UserIdAsync(Lazer(services, token).Request));

        await ExecuteAsync("UPDATE oauth_access_tokens SET revoked = 1");
        Assert.Null(await tokens.UserIdAsync(Lazer(services, token).Request));
        Assert.Null(await tokens.UserIdAsync(Lazer(services, await SignInAsync(services, mapper.Id, audience: "7")).Request));
        Assert.Null(await tokens.UserIdAsync(Lazer(services, await SignInAsync(services, mapper.Id, key: RSA.Create(2048))).Request));
        Assert.Null(await tokens.UserIdAsync(new DefaultHttpContext().Request));
    }

    // ── set-up ─────────────────────────────────────────────────────────────────────────────

    private RSA _signingKey = null!;

    private async Task<(BeatmapSubmissions Submissions, ServiceProvider Services, ICustomBeatmapSetRepository Sets)> SetUpAsync()
    {
        await NewTomYumContextAsync();
        Directory.CreateDirectory(_directory);
        _signingKey = RSA.Create(2048);
        var keyPath = Path.Combine(_directory, "oauth-public.key");
        await File.WriteAllTextAsync(keyPath, _signingKey.ExportSubjectPublicKeyInfoPem());

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [CustomBeatmapStore.PathKey] = Path.Combine(_directory, "beatmaps"),
            ["OsuWeb:OAuthPublicKeyPath"] = keyPath,
        }).Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddLogging()
            .AddScoped(_ => NewOsuWebContext())
            .AddScoped<IUserRepository>(_ => new OsuWebUserRepository(NewOsuWebContext()))
            .AddSingleton<IPasswordHasher>(Passwords)
            .AddSingleton<LazerTokens>()
            .AddScoped<IBeatmapRepository>(_ => Beatmaps())
            .BuildServiceProvider();
        _services = services;
        return (NewSubmissions(new RestrictedUsers()), services, new CustomBeatmapSetRepository(NewTomYumContext()));
    }

    private ServiceProvider _services = null!;

    private OsuWebMirroringBeatmapRepository Beatmaps()
    {
        var osuWeb = NewOsuWebContext();
        return new OsuWebMirroringBeatmapRepository(new BeatmapRepository(NewTomYumContext()), new OsuWebBeatmapMirror(osuWeb), osuWeb);
    }

    private BeatmapSubmissions NewSubmissions(RestrictedUsers restricted)
    {
        var configuration = _services.GetRequiredService<IConfiguration>();
        var store = new CustomBeatmapStore(configuration);
        var files = new OsuFileCache(Path.Combine(_directory, "osu"), new HttpClient(), NullLogger<OsuFileCache>.Instance);
        var sets = new CustomBeatmapSetRepository(NewTomYumContext());
        var beatmaps = Beatmaps();
        var importer = new CustomBeatmapImporter(beatmaps, new OsuWebUserRepository(NewOsuWebContext()), store, files, TimeProvider.System,
            NullLogger<CustomBeatmapImporter>.Instance,
            new OfficialPerformanceCalculator(files, new MemoryCache(new MemoryCacheOptions()), NullLogger<OfficialPerformanceCalculator>.Instance),
            website: new OsuWebCustomSets(NewOsuWebContext()), sets: sets);
        return new BeatmapSubmissions(sets, beatmaps, store, importer, restricted, TimeProvider.System, new OsuWebCustomSets(NewOsuWebContext()));
    }

    private async Task<User> AddUserAsync(string name) =>
        await new OsuWebUserRepository(NewOsuWebContext()).AddAsync(new User
        {
            Username = name,
            SafeName = User.MakeSafeName(name),
            Email = $"{name.ToLowerInvariant()}@x.dev",
            PasswordBcrypt = Passwords.HashPassword(PasswordMd5),
            Country = "nl",
        });

    /// <summary>A lazer login: a token signed like the website signs them, and its row.</summary>
    private async Task<string> SignInAsync(ServiceProvider services, int userId, string audience = "5", RSA? key = null)
    {
        _ = services;
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(40));
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Audience = audience,
            Claims = new Dictionary<string, object> { ["jti"] = id, ["sub"] = userId.ToString(CultureInfo.InvariantCulture), ["scopes"] = AllScopes },
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key ?? _signingKey), SecurityAlgorithms.RsaSha256),
        });
        await ExecuteAsync(string.Create(CultureInfo.InvariantCulture,
            $"INSERT INTO oauth_access_tokens (id, user_id, client_id, revoked, expires_at) VALUES ('{id}', {userId}, 5, 0, UTC_TIMESTAMP() + INTERVAL 1 DAY)"));
        return token;
    }

    private async Task ExecuteTomYumAsync(string sql)
    {
        await using var db = NewTomYumContext();
        await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database, sql);
    }

    // ── requests ───────────────────────────────────────────────────────────────────────────

    private static DefaultHttpContext Stable(ServiceProvider services, string query) =>
        new DefaultHttpContext { RequestServices = services, Request = { QueryString = new QueryString("?" + query) } };

    private static DefaultHttpContext StableUpload(ServiceProvider services, byte[] osz2, string fields)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.ContentType = "multipart/form-data; boundary=x";
        var values = fields.Split('&').Select(f => f.Split('=', 2)).ToDictionary(f => f[0], f => new StringValues(f[1]));
        context.Request.Form = new FormCollection(values, new FormFileCollection { new FormFile(new MemoryStream(osz2), 0, osz2.Length, "0", "set.osz2") });
        return context;
    }

    /// <summary>The editor's post; <paramref name="loginInQuery"/>: its login as getid's and upload's (<c>u</c>, <c>h</c> in the query).</summary>
    private static DefaultHttpContext StablePost(ServiceProvider services, string user, string set, bool complete, bool loginInQuery = false)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.ContentType = "application/x-www-form-urlencoded";
        if (loginInQuery)
            context.Request.QueryString = new QueryString($"?u={user}&h={PasswordMd5}");
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["u"] = loginInQuery ? StringValues.Empty : user,
            ["p"] = loginInQuery ? StringValues.Empty : PasswordMd5,
            ["b"] = set,
            ["subject"] = "Artist - Song",
            ["message"] = "[generated header]\n---------------\nMy first map!",
            ["complete"] = complete ? "1" : "0",
            ["notify"] = "1",
        });
        return context;
    }

    private static DefaultHttpContext Lazer(ServiceProvider services, string token, (string Field, string Name, byte[] Bytes)? file = null, (string Field, string Value)? form = null)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers.Authorization = "Bearer " + token;
        context.Request.ContentType = "multipart/form-data; boundary=x";
        var files = new FormFileCollection();
        if (file is { } f)
            files.Add(new FormFile(new MemoryStream(f.Bytes), 0, f.Bytes.Length, f.Field, f.Name) { Headers = new HeaderDictionary(), ContentDisposition = $"form-data; name=\"{f.Field}\"; filename=\"{f.Name}\"" });
        var values = form is { } v ? new Dictionary<string, StringValues> { [v.Field] = v.Value } : [];
        context.Request.Form = new FormCollection(values, files);
        return context;
    }

    private static async Task<string> Text(Task<IResult> result) => Assert.IsType<ContentHttpResult>(await result).ResponseContent!;

    private static Task<string> Json(IResult result)
    {
        var ok = Assert.IsAssignableFrom<IValueHttpResult>(result);
        return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(ok.Value));
    }

    private static int StatusOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    // CA5351: osu!'s checksums are MD5 (the client and the server compare them); not a security primitive.
#pragma warning disable CA5351
    private static string Md5(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
#pragma warning restore CA5351

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

    /// <summary>A small real map with its ids written in, as a client exports it.</summary>
    private static string Osu(string version, int beatmapId, int setId, int shift = 0)
    {
        var objects = new StringBuilder();
        for (var i = 0; i < 20; i++)
            objects.Append(CultureInfo.InvariantCulture, $"{64 + (i % 4) * 128},192,{1000 + shift + i * 250},1,0,0:0:0:0:\n");
        return $"""
            osu file format v14

            [General]
            AudioFilename: audio.mp3
            Mode: 0

            [Metadata]
            Title:Song
            Artist:Artist
            Creator:Mapper
            Version:{version}
            BeatmapID:{beatmapId}
            BeatmapSetID:{setId}

            [Difficulty]
            HPDrainRate:5
            CircleSize:4
            OverallDifficulty:8
            ApproachRate:9

            [TimingPoints]
            0,500,4,2,0,50,1,0

            [HitObjects]
            {objects}
            """.ReplaceLineEndings("\n");
    }
}
