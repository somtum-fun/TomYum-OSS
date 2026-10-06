using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Services;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>
/// Device verification at login (ClientVerification): builds that can verify (b20151228 on, or old ones
/// patched with the tomyum-verify capability) are held with -8; older ones follow OldClients.
/// </summary>
public sealed class ClientVerificationTests : OsuWebDatabaseTest
{
    private const string Domain = "example.test";

    // Each login that gets in is logged out again (one game client per account).
    private readonly PlayerRegistry _registry = new();

    [MySqlFact]
    public async Task Refuse_OldClientsOnlyWhenTheDeviceNeedsVerifying()
    {
        var (login, users) = NewLogin(OldClientPolicy.Refuse);
        var user = await AddUserAsync(users, "Player");
        var home = Device();
        var laptop = Device();

        // The account's first device is trusted, whatever the build.
        Assert.Equal(user.Id, Reply(await LogInAsync(login, "b20140624", home)).UserId);

        // A new device on an unpatched old build: refused, pointed at the website and the downloads.
        var refused = Reply(await LogInAsync(login, "b20140624", laptop));
        Assert.Equal(-1, refused.UserId);
        Assert.Contains($"https://osu.{Domain}/p/verify", refused.Notice);
        Assert.Contains($"https://osu.{Domain}/home/download", refused.Notice);

        // Trying again stamps the waiting device again (the website offers the one that tried last, in the last day).
        await ExecuteAsync($"UPDATE osu_user_security SET timestamp = NOW() - INTERVAL 2 DAY WHERE user_id = {user.Id} AND verified = 0");
        Assert.Equal(-1, Reply(await LogInAsync(login, "b20140624", laptop)).UserId);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT COUNT(*) FROM osu_user_security WHERE user_id = {user.Id} AND verified = 0 AND timestamp > NOW() - INTERVAL 1 HOUR"));

        // The same device on a build patched for it, or one that knows -8: held, the notice only once.
        var held = Reply(await LogInAsync(login, "b20140624", laptop, "|tomyum-verify"));
        Assert.Equal(-8, held.UserId);
        Assert.Contains($"https://osu.{Domain}/p/verify", held.Notice);
        var again = Reply(await LogInAsync(login, "b20151228.3", laptop));
        Assert.Equal(-8, again.UserId);
        Assert.Null(again.Notice);

        // Confirmed on the website: every build gets in from it.
        await ExecuteAsync($"UPDATE osu_user_security SET verified = 1 WHERE user_id = {user.Id}");
        Assert.Equal(user.Id, Reply(await LogInAsync(login, "b20140624", laptop)).UserId);

        // A build with no hardware hashes can't be checked: a new device for an account that has one.
        var hashless = Reply(await LogInAsync(login, "b1122", null));
        Assert.Equal(-1, hashless.UserId);
        Assert.Contains($"https://osu.{Domain}/home/download", hashless.Notice);
        // b20120522/b20120529 as osu! made them send no disk signature (4 parts): the same, even with the capability.
        Assert.Equal(-1, Reply(await LogInAsync(login, "b20120522", $"{Md5()}:1c.1b.0d:{Md5()}:{Md5()}:", "|tomyum-verify")).UserId);

        // ... but an account's first.
        await AddUserAsync(users, "Newcomer");
        Assert.True(Reply(await LogInAsync(login, "b1122", null, name: "Newcomer")).UserId > 0);
    }

    [MySqlTheory]
    [InlineData("b337.test")]   // 2008: the login line had no city, no client hash
    [InlineData("b1122")]       // no client hash
    [InlineData("b1218")]
    [InlineData("b1818")]       // a 3-part client hash (no uninstall id, no disk)
    [InlineData("b20120522")]   // a 4-part one (no disk)
    public async Task Refuse_PatchedBuildsBeforeB20120812_VerifyTheirDevice(string version)
    {
        var (login, users) = NewLogin(OldClientPolicy.Refuse);
        var name = "Old" + version.Replace(".", string.Empty, StringComparison.Ordinal);
        var user = await AddUserAsync(users, name);
        var home = Device();
        var laptop = Device();
        Assert.Equal(user.Id, Reply(await LogInAsync(login, "b20140624", home, name: name)).UserId);

        // As osu! made it (no device): refused, pointed at the downloads.
        var unpatched = Reply(await LogInAsync(login, version, null, name: name));
        Assert.Equal(-1, unpatched.UserId);
        Assert.Contains($"https://osu.{Domain}/home/download", unpatched.Notice);

        // Patched (step 12 of the prod patcher): its whole device and the capability. A new device: held.
        var held = Reply(await LogInAsync(login, version, laptop, "|tomyum-verify", name: name));
        Assert.Equal(-8, held.UserId);
        Assert.Contains($"https://osu.{Domain}/p/verify", held.Notice);
        Assert.Equal(1L, await ScalarAsync<long>($"SELECT COUNT(*) FROM osu_user_security WHERE user_id = {user.Id} AND verified = 0"));
        // Its retries (every 15 s) are held quietly.
        Assert.Null(Reply(await LogInAsync(login, version, laptop, "|tomyum-verify", name: name)).Notice);

        // Confirmed on the website (what /p/verify?ch= marks): the next retry gets in, and so does every later login.
        await ExecuteAsync($"UPDATE osu_user_security SET verified = 1 WHERE user_id = {user.Id}");
        Assert.Equal(user.Id, Reply(await LogInAsync(login, version, laptop, "|tomyum-verify", name: name)).UserId);
        Assert.Equal(user.Id, Reply(await LogInAsync(login, version, laptop, "|tomyum-verify", name: name)).UserId);
    }

    [MySqlFact]
    public async Task Refuse_TheCapabilityAloneSkipsNothing()
    {
        var (login, users) = NewLogin(OldClientPolicy.Refuse);
        var user = await AddUserAsync(users, "Guarded");
        await AddUserAsync(users, "Other");
        Assert.Equal(user.Id, Reply(await LogInAsync(login, "b20140624", Device(), name: "Guarded")).UserId);

        // The capability with no device, or one that isn't md5s: still a build that sends no device.
        Assert.Equal(-1, Reply(await LogInAsync(login, "b1818", string.Empty, "|tomyum-verify", name: "Guarded")).UserId);
        Assert.Equal(-1, Reply(await LogInAsync(login, "b337.test", "x:y:z:w:v", "|tomyum-verify", name: "Guarded")).UserId);

        // A made-up device is a new device: held on every try until the player confirms it.
        var madeUp = $"{Md5()}::{Md5()}:{Md5()}:{Md5()}";
        for (var i = 0; i < 3; i++)
            Assert.Equal(-8, Reply(await LogInAsync(login, "b1818", madeUp, "|tomyum-verify", name: "Guarded")).UserId);

        // A device confirmed by one account isn't confirmed for another.
        var shared = $"{Md5()}:1c.1b.0d:{Md5()}:{Md5()}:{Md5()}";
        Assert.True(Reply(await LogInAsync(login, "b1818", shared, "|tomyum-verify", name: "Other")).UserId > 0);   // Other's first device
        Assert.Equal(-8, Reply(await LogInAsync(login, "b1818", shared, "|tomyum-verify", name: "Guarded")).UserId);
    }

    [MySqlFact]
    public async Task Unverified_OldClientsGetIn()
    {
        var (login, users) = NewLogin(OldClientPolicy.Unverified, downloadsUrl: "https://dl.example.test/");
        var user = await AddUserAsync(users, "Player");
        var home = Device();
        var laptop = Device();

        Assert.Equal(user.Id, Reply(await LogInAsync(login, "b20140624", home)).UserId);
        Assert.Equal(user.Id, Reply(await LogInAsync(login, "b20140624", laptop)).UserId);
        Assert.Equal(user.Id, Reply(await LogInAsync(login, "b1122", null)).UserId);

        // Today's client always sends its device: one that doesn't can't skip the check.
        Assert.Equal(-1, Reply(await LogInAsync(login, "b20250101", null)).UserId);
        Assert.Equal(-1, Reply(await LogInAsync(login, "b20250101", "x:y:z:w:v:")).UserId);

        // Today's client (and patched old ones) still verify it.
        Assert.Equal(-8, Reply(await LogInAsync(login, "b20250101", laptop)).UserId);
        Assert.Equal(-8, Reply(await LogInAsync(login, "b20130606.1", laptop, "|tomyum-verify")).UserId);
    }

    [Fact]
    public void TheNotices_NameTheConfiguredDomain()
    {
        Assert.Contains("https://osu.example.test/p/verify", BanchoLoginHandler.VerificationNotice("example.test"));
        Assert.Contains("https://dl.example.test", BanchoLoginHandler.OldClientNotice("example.test", "https://dl.example.test", hasDevice: false));
        Assert.DoesNotContain("blueskychan", BanchoLoginHandler.VerificationNotice("example.test"));
    }

    private (BanchoLoginHandler, OsuWebUserRepository) NewLogin(OldClientPolicy policy, string? downloadsUrl = null)
    {
        var users = new OsuWebUserRepository(NewOsuWebContext());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [BanchoHosts.DomainConfigKey] = Domain })
            .Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider();
        var options = Microsoft.Extensions.Options.Options.Create(new ClientVerificationOptions { Enabled = true, OldClients = policy, DownloadsUrl = downloadsUrl });
        var login = new BanchoLoginHandler(users, new BcryptPasswordHasher(), new InMemoryUserStatsRepository(), _registry, new ChannelRegistry(),
            NullLogger<BanchoLoginHandler>.Instance, clientVerificationOptions: options, clientVerification: new OsuWebClientVerification(NewOsuWebContext()), services: services);
        return (login, users);
    }

    private static async Task<User> AddUserAsync(OsuWebUserRepository users, string name)
    {
        var hasher = new BcryptPasswordHasher();
        return await users.AddAsync(new User
        {
            Username = name, SafeName = User.MakeSafeName(name), Email = $"{name}@x.dev",
            PasswordBcrypt = hasher.HashPassword(hasher.ComputeMd5Hex("password")), Country = "th",
        });
    }

    /// <summary>A device's hashes: osu! path, adapters, uninstall id, disk (unique per test: held devices are kept statically).</summary>
    private static string Device() =>
        $"{Md5()}:1c.1b.0d:{Md5()}:{Md5()}:{Md5()}:";

    private static string Md5() => Guid.NewGuid().ToString("N"); // 32 hex digits, as an md5

    /// <summary>Logs in as <paramref name="name"/> with <paramref name="version"/>: no hashes (pre-2012 builds) when <paramref name="device"/> is null.</summary>
    private async Task<BanchoLoginHandler.LoginOutcome> LogInAsync(BanchoLoginHandler login, string version, string? device, string capabilities = "", string name = "Player")
    {
        var info = device is null ? $"{version}|7|0" : $"{version}|7|0|{device}|0{capabilities}";
        var outcome = await login.HandleAsync($"{name}\n{new BcryptPasswordHasher().ComputeMd5Hex("password")}\n{info}\n", "TH", CancellationToken.None);
        if (outcome.Token is { } token)
            _registry.Remove(token);
        return outcome;
    }

    private static (int UserId, string? Notice) Reply(BanchoLoginHandler.LoginOutcome outcome)
    {
        int? userId = null;
        string? notice = null;
        var reader = new BanchoPacketReader(outcome.Body);
        while (reader.CanReadHeader)
        {
            var header = reader.ReadHeader();
            if ((ushort)header.Id == (ushort)ServerPacketId.UserId && userId is null)
                userId = reader.ReadInt32();
            else if ((ushort)header.Id == (ushort)ServerPacketId.Notification && userId is null)
                notice = reader.ReadString();
            else
                reader.Skip(checked((int)header.Length));
        }
        return (userId ?? int.MinValue, notice);
    }
}
