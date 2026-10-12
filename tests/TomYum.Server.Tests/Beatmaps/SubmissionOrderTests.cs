using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Performance;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>Beatmap submissions: who may upload is checked before a package is unpacked, and only a few are unpacked at once.</summary>
public sealed class SubmissionOrderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tomyum-submit-" + Guid.NewGuid().ToString("N"));
    private readonly BcryptPasswordHasher _hasher = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task SomeoneElsesSet_IsRefused_BeforeTheUploadIsUnpacked()
    {
        var (services, submissions, _) = await SetUpAsync(ownerId: 99);

        // Not an .osz2 at all: unpacking it would say so; the refusal comes first.
        var answer = await Text(SubmissionEndpoints.StableUploadAsync(Upload(services, [1, 2, 3, 4]), submissions, default));

        Assert.Equal("1\nThat beatmap set isn't yours.", answer);
    }

    [Fact]
    public async Task ARestrictedPlayer_IsRefused_BeforeTheUploadIsUnpacked()
    {
        var restricted = new RestrictedUsers();
        var (services, submissions, user) = await SetUpAsync(ownerId: null, restricted);
        restricted.Set(user.Id, true);

        var answer = await Text(SubmissionEndpoints.StableUploadAsync(Upload(services, [1, 2, 3, 4]), submissions, default));

        Assert.Equal("5\nYour account is currently restricted.", answer);
    }

    [Fact]
    public async Task OnlyAFewPackages_AreUnpackedAtOnce()
    {
        var running = 0;
        var most = 0;
        using var release = new ManualResetEventSlim();
        // Their own threads: each blocks while it "unpacks".
        var work = Enumerable.Range(0, BeatmapSubmissions.MaxDecoding + 3).Select(_ => Task.Factory.StartNew(() => BeatmapSubmissions.DecodeAsync(() =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            release.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Decrement(ref running);
            return now;
        }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToList();

        // Once the places are taken, the others wait however long it takes.
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref running) == BeatmapSubmissions.MaxDecoding, TimeSpan.FromSeconds(10)));
        await Task.Delay(200);
        release.Set();
        await Task.WhenAll(work);

        Assert.Equal(BeatmapSubmissions.MaxDecoding, most);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private async Task<(ServiceProvider Services, BeatmapSubmissions Submissions, User User)> SetUpAsync(int? ownerId, RestrictedUsers? restricted = null)
    {
        var users = new InMemoryUserRepository();
        var user = await users.AddAsync(new User
        {
            Username = "Mapper", SafeName = "mapper", Email = "m@x.dev", Country = "nl",
            PasswordBcrypt = _hasher.HashPassword(_hasher.ComputeMd5Hex("pw")),
        });
        var services = new ServiceCollection()
            .AddSingleton<IUserRepository>(users)
            .AddSingleton<IPasswordHasher>(_hasher)
            .BuildServiceProvider();

        var sets = DispatchProxy.Create<ICustomBeatmapSetRepository, OneSet>();
        ((OneSet)(object)sets).Set = new CustomBeatmapSet { SetId = Beatmap.CustomIdFloor, OwnerId = ownerId ?? user.Id };
        var store = new CustomBeatmapStore(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [CustomBeatmapStore.PathKey] = Path.Combine(_root, "beatmaps"),
        }).Build());
        var importer = new CustomBeatmapImporter(PackageNamesTests.NoBeatmaps(), users, store,
            new OsuFileCache(Path.Combine(_root, "osu"), new HttpClient(), NullLogger<OsuFileCache>.Instance),
            TimeProvider.System, NullLogger<CustomBeatmapImporter>.Instance);
        var submissions = new BeatmapSubmissions(sets, PackageNamesTests.NoBeatmaps(), store, importer, restricted ?? new RestrictedUsers(), TimeProvider.System);
        return (services, submissions, user);
    }

    private DefaultHttpContext Upload(ServiceProvider services, byte[] osz2)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.ContentType = "multipart/form-data; boundary=x";
        context.Request.Form = new FormCollection(
            new Dictionary<string, StringValues> { ["u"] = "Mapper", ["h"] = _hasher.ComputeMd5Hex("pw"), ["s"] = Beatmap.CustomIdFloor.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            new FormFileCollection { new FormFile(new MemoryStream(osz2), 0, osz2.Length, "0", "set.osz2") });
        return context;
    }

    private static async Task<string> Text(Task<IResult> result) => Assert.IsType<ContentHttpResult>(await result).ResponseContent!;

    /// <summary>A set repository with one set (anything else isn't expected).</summary>
    public class OneSet : DispatchProxy
    {
        public CustomBeatmapSet? Set { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(ICustomBeatmapSetRepository.GetAsync) => Task.FromResult(Set),
            _ => throw new InvalidOperationException($"{targetMethod?.Name} wasn't expected."),
        };
    }
}
