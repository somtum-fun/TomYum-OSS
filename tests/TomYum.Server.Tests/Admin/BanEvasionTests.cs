using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests.Admin;

/// <summary>Hardware hashes at login and the first-login ban-evasion check (<see cref="BanchoLoginHandler"/>).</summary>
public sealed class BanEvasionTests
{
    private const string PathA = "11111111111111111111111111111111";
    private const string AdaptersA = "22222222222222222222222222222222";
    private const string UninstallA = "33333333333333333333333333333333";
    private const string DiskA = "44444444444444444444444444444444";
    private const string Other = "99999999999999999999999999999999";
    private const string Md5Zero = "cfcd208495d565ef66e7dff9f98764da";

    [Fact]
    public void Match_UsesAllHashes_ButOnlyTheUninstallIdUnderWine()
    {
        Assert.Equal(new ClientHashQuery(AdaptersA, UninstallA, DiskA), ClientHashMatch.For("1c.1b.0d", AdaptersA, UninstallA, DiskA));
        Assert.Equal(new ClientHashQuery(null, UninstallA, null), ClientHashMatch.For("runningunderwine", AdaptersA, UninstallA, DiskA));
    }

    [Theory]
    [InlineData(Md5Zero)]                               // a disk without a serial
    [InlineData("d41d8cd98f00b204e9800998ecf8427e")]    // md5("")
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("1234")]
    public void MeaninglessHashes_NeverMatch(string hash) => Assert.Null(ClientHashMatch.Usable(hash));

    [Fact]
    public void Hashes_AreNormalised() =>
        Assert.Equal("abcdef00abcdef00abcdef00abcdef00", ClientHashMatch.Usable(" ABCDEF00abcdef00ABCDEF00abcdef00 "));

    [Fact]
    public async Task NewAccount_OnARestrictedPlayersMachine_IsRefused_WithTheNotice()
    {
        var (handler, registry, users, hashes) = NewLogin();
        var cheater = await AddUserAsync(users, "Cheater");
        await LogInAsync(handler, "Cheater", AdaptersA, UninstallA, DiskA);
        await RestrictAsync(users, cheater);
        var fresh = await AddUserAsync(users, "Fresh");

        var outcome = await LogInAsync(handler, "Fresh", Other, Other, DiskA);

        Assert.Null(outcome.Token);
        Assert.Null(registry.GetByUserId(fresh.Id));
        Assert.Equal(BanchoLoginHandler.EvasionNotice, Notice(outcome.Body));
        Assert.Equal(-1, UserIdPacket(outcome.Body));
        // Still recorded, so staff see the attempt with !alts; still unverified.
        Assert.Single(await hashes.ListAsync(fresh.Id));
        Assert.False(await hashes.IsVerifiedAsync(fresh.Id));
    }

    [Fact]
    public async Task NewAccount_SharingWithAnUnrestrictedPlayer_GetsIn_AndIsVerified()
    {
        var (handler, _, users, hashes) = NewLogin();
        await AddUserAsync(users, "Sibling");
        await LogInAsync(handler, "Sibling", AdaptersA, UninstallA, DiskA);
        var fresh = await AddUserAsync(users, "Fresh");

        var outcome = await LogInAsync(handler, "Fresh", AdaptersA, UninstallA, DiskA);

        Assert.NotNull(outcome.Token);
        Assert.True(await hashes.IsVerifiedAsync(fresh.Id));
    }

    [Fact]
    public async Task VerifiedAccount_IsNotCheckedAgain()
    {
        var (handler, registry, users, _) = NewLogin();
        var cheater = await AddUserAsync(users, "Cheater");
        var regular = await AddUserAsync(users, "Regular");
        registry.Remove((await LogInAsync(handler, "Regular", Other, Other, Other)).Token!);

        // Later the regular's machine turns out to be shared with a restricted account.
        await LogInAsync(handler, "Cheater", AdaptersA, UninstallA, DiskA);
        await RestrictAsync(users, cheater);
        var outcome = await LogInAsync(handler, "Regular", AdaptersA, UninstallA, DiskA);

        Assert.NotNull(outcome.Token);
        Assert.NotNull(registry.GetByUserId(regular.Id));
    }

    [Theory]
    [InlineData("runningunderwine", AdaptersA, Other, DiskA, true)]  // Wine: adapters and disk don't count
    [InlineData("runningunderwine", Other, UninstallA, Other, false)]
    [InlineData("1c.1b", Other, Other, Md5Zero, true)]              // md5("0") disk: everyone has it
    [InlineData("1c.1b", AdaptersA, Other, Other, false)]
    public async Task Guards(string adaptersStr, string adapters, string uninstall, string disk, bool getsIn)
    {
        var (handler, _, users, _) = NewLogin();
        var cheater = await AddUserAsync(users, "Cheater");
        await LogInAsync(handler, "Cheater", AdaptersA, UninstallA, DiskA);
        await LogInAsync(handler, "Cheater", Other.Replace('9', '8'), Other.Replace('9', '7'), Md5Zero);
        await RestrictAsync(users, cheater);
        await AddUserAsync(users, "Fresh");

        var outcome = await LogInAsync(handler, "Fresh", adapters, uninstall, disk, adaptersStr);

        Assert.Equal(getsIn, outcome.Token is not null);
    }

    [Fact]
    public async Task Staff_AreNeverChecked()
    {
        var (handler, _, users, _) = NewLogin();
        var cheater = await AddUserAsync(users, "Cheater");
        await LogInAsync(handler, "Cheater", AdaptersA, UninstallA, DiskA);
        await RestrictAsync(users, cheater);
        await AddUserAsync(users, "Helper", UserPrivileges.Normal | UserPrivileges.Moderator);

        Assert.NotNull((await LogInAsync(handler, "Helper", AdaptersA, UninstallA, DiskA)).Token);
    }

    [Fact]
    public async Task Occurrences_Count_PerCombination()
    {
        var (handler, registry, users, hashes) = NewLogin();
        var player = await AddUserAsync(users, "Player");
        for (var i = 0; i < 3; i++)
            registry.Remove((await LogInAsync(handler, "Player", AdaptersA, UninstallA, DiskA)).Token!);
        await LogInAsync(handler, "Player", Other, UninstallA, DiskA);

        var seen = await hashes.ListAsync(player.Id);
        Assert.Equal([1, 3], seen.Select(h => h.Occurrences).Order());
    }

    [Fact]
    public async Task StaffCommands_ListAlts_AndVerifyARefusedAccount()
    {
        var (handler, registry, users, hashes) = NewLogin();
        var cheater = await AddUserAsync(users, "Cheater");
        await LogInAsync(handler, "Cheater", AdaptersA, UninstallA, DiskA);
        await RestrictAsync(users, cheater);
        await AddUserAsync(users, "Fresh");
        Assert.Null((await LogInAsync(handler, "Fresh", AdaptersA, Other, Other)).Token);
        var gmt = await AddUserAsync(users, "Gmt", UserPrivileges.Normal | UserPrivileges.Moderator);
        var staffLog = new RecordingStaffLog();
        var services = new ServiceCollection()
            .AddSingleton<IUserRepository>(users)
            .AddSingleton<IClientHashRepository>(hashes)
            .AddSingleton<IStaffLogRepository>(staffLog)
            .BuildServiceProvider();
        var commands = new ModerationCommands(services.GetRequiredService<IServiceScopeFactory>(), registry);

        Assert.Equal("Same machine as Fresh: Cheater (restricted).", await commands.RunAsync(gmt.Id, "alts", ["Fresh"]));
        Assert.Equal("No other accounts seen on Gmt's machine(s).", await commands.RunAsync(gmt.Id, "alts", ["Gmt"]));
        Assert.Equal("That command is staff-only.", await commands.RunAsync(cheater.Id, "verify", ["Cheater"]));
        Assert.StartsWith("Verified Fresh", await commands.RunAsync(gmt.Id, "verify", ["Fresh"]));
        Assert.Equal("Fresh is already verified.", await commands.RunAsync(gmt.Id, "verify", ["Fresh"]));
        Assert.Equal("user.verify", Assert.Single(staffLog.Entries).Action);

        Assert.NotNull((await LogInAsync(handler, "Fresh", AdaptersA, Other, Other)).Token);
    }

    private static (BanchoLoginHandler, PlayerRegistry, InMemoryUserRepository, InMemoryClientHashRepository) NewLogin()
    {
        var users = new InMemoryUserRepository();
        var registry = new PlayerRegistry();
        var hashes = new InMemoryClientHashRepository();
        var handler = new BanchoLoginHandler(users, new BcryptPasswordHasher(), new InMemoryUserStatsRepository(), registry,
            new ChannelRegistry(), NullLogger<BanchoLoginHandler>.Instance, clientHashes: hashes);
        return (handler, registry, users, hashes);
    }

    private static Task<BanchoLoginHandler.LoginOutcome> LogInAsync(
        BanchoLoginHandler handler, string username, string adapters, string uninstall, string disk, string adaptersStr = "1c.1b.0d")
    {
        var body = $"{username}\n{new BcryptPasswordHasher().ComputeMd5Hex("password")}\nb20231228.1|0|0|{PathA}:{adaptersStr}:{adapters}:{uninstall}:{disk}:|0\n";
        return handler.HandleAsync(body, "TH", CancellationToken.None);
    }

    private static async Task<User> AddUserAsync(InMemoryUserRepository users, string name, UserPrivileges privileges = UserPrivileges.Normal)
    {
        var hasher = new BcryptPasswordHasher();
        return await users.AddAsync(new User
        {
            Username = name,
            Email = $"{name.ToLowerInvariant()}@tomyum.dev",
            PasswordBcrypt = hasher.HashPassword(hasher.ComputeMd5Hex("password")),
            Privileges = privileges,
        });
    }

    private static Task<User> RestrictAsync(InMemoryUserRepository users, User user) =>
        users.UpdateAsync(new User
        {
            Id = user.Id,
            Username = user.Username,
            SafeName = user.SafeName,
            Email = user.Email,
            PasswordBcrypt = user.PasswordBcrypt,
            Privileges = user.Privileges,
            IsRestricted = true,
        });

    private static string? Notice(byte[] body)
    {
        var reader = new BanchoPacketReader(body);
        while (reader.CanReadHeader)
        {
            var header = reader.ReadHeader();
            if ((ushort)header.Id == (ushort)ServerPacketId.Notification)
                return reader.ReadString();
            reader.ReadBytes((int)header.Length);
        }
        return null;
    }

    private static int? UserIdPacket(byte[] body)
    {
        var reader = new BanchoPacketReader(body);
        while (reader.CanReadHeader)
        {
            var header = reader.ReadHeader();
            if ((ushort)header.Id == (ushort)ServerPacketId.UserId)
                return reader.ReadInt32();
            reader.ReadBytes((int)header.Length);
        }
        return null;
    }

    private sealed class RecordingStaffLog : IStaffLogRepository
    {
        public List<StaffLogEntry> Entries { get; } = [];

        public Task AddAsync(StaffLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StaffLogEntry>> ListAsync(StaffLogQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StaffLogEntry>>(Entries);
    }
}
