using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Services;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Admin;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Admin;

/// <summary>
/// Restrictions and silences in-game: login, chat, visibility to others, and applying a change
/// live (<see cref="UserModerationSync"/>).
/// </summary>
public sealed class ModerationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, true)]  // banned account type
    [InlineData(0, true, true)]   // warnings
    public void Restricted_FollowsOsuWeb(int userType, bool warnings, bool restricted)
    {
        Assert.Equal(restricted, OsuWebAccounts.IsRestricted((sbyte)userType, warnings));
    }

    [Fact]
    public void SilenceEnd_IsStartPlusPeriod_WhileItLasts()
    {
        var start = Now.UtcDateTime.AddMinutes(-10);

        Assert.Equal(new DateTimeOffset(start).AddHours(1), OsuWebAccounts.SilenceEnd(start, 3600, Now));
        Assert.Null(OsuWebAccounts.SilenceEnd(start, 60, Now));      // over
        Assert.Null(OsuWebAccounts.SilenceEnd(start, 0, Now));       // lifted
        Assert.Null(OsuWebAccounts.SilenceEnd(null, 3600, Now));     // none
    }

    [Fact]
    public async Task Login_Restricted_GetsTheNoticeAndIsntAnnounced()
    {
        var (handler, registry, users) = NewLogin();
        var watcher = registry.Add(new BanchoPresence { UserId = 50, Username = "Watcher" });
        await AddUserAsync(users, "Cheater", restricted: true);

        var outcome = await handler.HandleAsync(LoginBody("Cheater"), "TH", CancellationToken.None);

        Assert.Contains((ushort)ServerPacketId.AccountRestricted, PacketIds(outcome.Body));
        Assert.DoesNotContain((ushort)ServerPacketId.UserPresence, PacketIds(watcher.DequeueAll()));
        Assert.True(registry.GetByUserId((await users.GetBySafeNameAsync("cheater"))!.Id)!.IsRestricted);
    }

    [Fact]
    public async Task Login_OthersDontSeeRestrictedPlayers()
    {
        var (handler, registry, users) = NewLogin();
        var hidden = registry.Add(new BanchoPresence { UserId = 50, Username = "Hidden" });
        hidden.IsRestricted = true;
        await AddUserAsync(users, "Normal");

        var outcome = await handler.HandleAsync(LoginBody("Normal"), "TH", CancellationToken.None);

        // Only their own presence: the restricted player isn't in the list.
        Assert.Single(PacketIds(outcome.Body), id => id == (ushort)ServerPacketId.UserPresence);
    }

    [Fact]
    public async Task Login_Silenced_GetsTheRemainingSeconds()
    {
        var (handler, _, users) = NewLogin();
        await AddUserAsync(users, "Loud", silenceEnd: DateTimeOffset.UtcNow.AddMinutes(30));

        var outcome = await handler.HandleAsync(LoginBody("Loud"), "TH", CancellationToken.None);

        var seconds = SilenceSeconds(outcome.Body);
        Assert.InRange(seconds, 1700u, 1800u);
    }

    [Fact]
    public async Task Chat_Silenced_IsNotDeliveredAndIsToldWhy()
    {
        var (registry, channels, sender, listener) = TwoInOsu();
        sender.SilenceEndUtc = Now.AddMinutes(5);
        var response = new BanchoPacketWriter(128);

        await new SendPublicMessageHandler(ChatTestBot()).HandleAsync(Context(sender, registry, channels, Message("hi", "#osu"), response));

        Assert.Empty(listener.DequeueAll());
        var reply = PacketIds(response.ToArray());
        Assert.Contains((ushort)ServerPacketId.SendMessage, reply);   // BanchoBot explains
        Assert.Contains((ushort)ServerPacketId.SilenceEnd, reply);
    }

    [Fact]
    public async Task Chat_Restricted_CantTalkPubliclyOrToOthers()
    {
        var (registry, channels, sender, listener) = TwoInOsu();
        sender.IsRestricted = true;

        await new SendPublicMessageHandler(ChatTestBot()).HandleAsync(Context(sender, registry, channels, Message("hi", "#osu"), new BanchoPacketWriter(64)));
        await new SendPrivateMessageHandler(ChatTestBot()).HandleAsync(Context(sender, registry, channels, Message("hi", "Listener"), new BanchoPacketWriter(64)));

        Assert.Empty(listener.DequeueAll());
    }

    [Fact]
    public async Task Refresh_Restrict_HidesThePlayerLive()
    {
        var (sync, registry, users, restricted) = NewSync();
        var user = await AddUserAsync(users, "Cheater");
        var player = registry.Add(new BanchoPresence { UserId = user.Id, Username = "Cheater", Privileges = BanchoPrivileges.Player });
        var other = registry.Add(new BanchoPresence { UserId = 99, Username = "Other" });
        await users.UpdateAsync(Copy(user, restricted: true));

        var result = await sync.RefreshAsync(user.Id);

        Assert.True(result!.Restricted);
        Assert.True(player.IsRestricted);
        Assert.True(restricted.Contains(user.Id));
        Assert.Contains((ushort)ServerPacketId.AccountRestricted, PacketIds(player.DequeueAll()));
        Assert.Contains((ushort)ServerPacketId.UserLogout, PacketIds(other.DequeueAll()));

        // And back: they reappear for others.
        await users.UpdateAsync(Copy(user, restricted: false));
        await sync.RefreshAsync(user.Id);
        Assert.False(player.IsRestricted);
        Assert.False(restricted.Contains(user.Id));
        Assert.Contains((ushort)ServerPacketId.UserPresence, PacketIds(other.DequeueAll()));
    }

    [Fact]
    public async Task Refresh_Silence_TellsThePlayerAndOthers()
    {
        var (sync, registry, users, _) = NewSync();
        var user = await AddUserAsync(users, "Loud");
        var player = registry.Add(new BanchoPresence { UserId = user.Id, Username = "Loud", Privileges = BanchoPrivileges.Player });
        var other = registry.Add(new BanchoPresence { UserId = 99, Username = "Other" });
        await users.UpdateAsync(Copy(user, silenceEnd: DateTimeOffset.UtcNow.AddHours(1)));

        await sync.RefreshAsync(user.Id);

        Assert.True(player.IsSilenced(DateTimeOffset.UtcNow));
        Assert.InRange(SilenceSeconds(player.DequeueAll()), 3500u, 3600u);
        Assert.Contains((ushort)ServerPacketId.UserSilenced, PacketIds(other.DequeueAll()));

        // Lifting it sends 0.
        await users.UpdateAsync(Copy(user, silenceEnd: null));
        await sync.RefreshAsync(user.Id);
        Assert.Equal(0u, SilenceSeconds(player.DequeueAll()));
    }

    [Fact]
    public async Task Refresh_Rename_MakesTheClientReconnect()
    {
        var (sync, registry, users, _) = NewSync();
        var user = await AddUserAsync(users, "OldName");
        var player = registry.Add(new BanchoPresence { UserId = user.Id, Username = "OldName", Privileges = BanchoPrivileges.Player });
        await users.UpdateAsync(new User
        {
            Id = user.Id, Username = "NewName", SafeName = "newname", Email = user.Email, PasswordBcrypt = user.PasswordBcrypt,
        });

        var result = await sync.RefreshAsync(user.Id);

        Assert.True(result!.Reconnected);
        Assert.Contains((ushort)ServerPacketId.Restart, PacketIds(player.DequeueAll()));
    }

    [Fact]
    public async Task Refresh_UnknownUser_ReturnsNull()
    {
        var (sync, _, _, _) = NewSync();

        Assert.Null(await sync.RefreshAsync(12345));
    }

    // --- helpers ---

    private static (BanchoLoginHandler, PlayerRegistry, InMemoryUserRepository) NewLogin()
    {
        var users = new InMemoryUserRepository();
        var registry = new PlayerRegistry();
        var handler = new BanchoLoginHandler(users, new BcryptPasswordHasher(), new InMemoryUserStatsRepository(), registry, new ChannelRegistry(), NullLogger<BanchoLoginHandler>.Instance);
        return (handler, registry, users);
    }

    private static (UserModerationSync, PlayerRegistry, InMemoryUserRepository, RestrictedUsers) NewSync()
    {
        var users = new InMemoryUserRepository();
        var registry = new PlayerRegistry();
        var restricted = new RestrictedUsers();
        return (new UserModerationSync(users, registry, restricted, TimeProvider.System, NullLogger<UserModerationSync>.Instance), registry, users, restricted);
    }

    private static async Task<User> AddUserAsync(InMemoryUserRepository users, string name, bool restricted = false, DateTimeOffset? silenceEnd = null)
    {
        var hasher = new BcryptPasswordHasher();
        return await users.AddAsync(new User
        {
            Username = name,
            Email = $"{name.ToLowerInvariant()}@tomyum.dev",
            PasswordBcrypt = hasher.HashPassword(hasher.ComputeMd5Hex("password")),
            IsRestricted = restricted,
            SilenceEndUtc = silenceEnd,
        });
    }

    private static User Copy(User user, bool restricted = false, DateTimeOffset? silenceEnd = null) => new()
    {
        Id = user.Id,
        Username = user.Username,
        SafeName = user.SafeName,
        Email = user.Email,
        PasswordBcrypt = user.PasswordBcrypt,
        IsRestricted = restricted,
        SilenceEndUtc = silenceEnd,
    };

    private static string LoginBody(string username) =>
        $"{username}\n{new BcryptPasswordHasher().ComputeMd5Hex("password")}\nb20231228.1|0|0|abc:runningunderwine:def:ghi:jkl:|0\n";

    private static (PlayerRegistry, ChannelRegistry, OnlinePlayer Sender, OnlinePlayer Listener) TwoInOsu()
    {
        var registry = new PlayerRegistry();
        var channels = new ChannelRegistry();
        var sender = registry.Add(new BanchoPresence { UserId = 2, Username = "Sender" });
        var listener = registry.Add(new BanchoPresence { UserId = 3, Username = "Listener" });
        channels.Get("#osu")!.Join(sender);
        channels.Get("#osu")!.Join(listener);
        return (registry, channels, sender, listener);
    }

    private static BanchoBot ChatTestBot() => new(new BanchoBotCommands(
        new TomYumVersion(), new PlayerRegistry(), new ChannelRegistry(), new MatchRegistry(),
        new BeatmapService(new NoMapsApi()), TestScopeFactory.Create(new InMemoryUserRepository()),
        new Plugins.PluginCommandRegistry(), new Plugins.LoadedPluginRegistry()));

    private static ClientPacketContext Context(OnlinePlayer player, PlayerRegistry registry, ChannelRegistry channels, byte[] payload, BanchoPacketWriter response) =>
        new(player, registry, channels, new MatchRegistry(), new ServiceCollection().BuildServiceProvider(), payload, response);

    private static byte[] Message(string text, string target)
    {
        var w = new BanchoPacketWriter(64);
        w.WriteString(string.Empty);
        w.WriteString(text);
        w.WriteString(target);
        w.WriteInt32(0);
        return w.ToArray();
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

    /// <summary>The value of the last SILENCE_END packet in <paramref name="body"/>.</summary>
    private static uint SilenceSeconds(byte[] body)
    {
        uint? seconds = null;
        var reader = new BanchoPacketReader(body);
        while (reader.CanReadHeader)
        {
            var header = reader.ReadHeader();
            if ((ushort)header.Id == (ushort)ServerPacketId.SilenceEnd)
                seconds = reader.ReadUInt32();
            else
                reader.Skip(checked((int)header.Length));
        }
        return seconds ?? throw new InvalidOperationException("No SILENCE_END packet.");
    }

    private sealed class NoMapsApi : IOsuApiClient
    {
        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public bool SupportsSince => false;
    }
}
