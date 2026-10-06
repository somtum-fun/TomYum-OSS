using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;
using TomYum.Server.Discord;
using Xunit;

namespace TomYum.Server.Tests.Discord;

/// <summary>The Discord bot's parts that don't need Discord or a database.</summary>
public sealed class DiscordTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LinkCodes_WorkOnce_AndExpire()
    {
        var time = new MovableTime(Now);
        var codes = new DiscordLinkCodes(time);

        var code = codes.Create(42);
        Assert.Matches("^[A-HJKMNP-Z2-9]{6}$", code);
        Assert.Equal(42UL, codes.Redeem(code.ToLowerInvariant()));
        Assert.Null(codes.Redeem(code)); // used up

        var late = codes.Create(42);
        time.Now += DiscordLinkCodes.Lifetime + TimeSpan.FromSeconds(1);
        Assert.Null(codes.Redeem(late));
    }

    [Fact]
    public void LinkCodes_ANewCode_ReplacesTheOldOne()
    {
        var codes = new DiscordLinkCodes(new MovableTime(Now));
        var first = codes.Create(7);
        var second = codes.Create(7);

        Assert.Null(codes.Redeem(first));
        Assert.Equal(7UL, codes.Redeem(second));
    }

    [Theory]
    [InlineData("Alpha", "hello", "**Alpha**: hello")]
    [InlineData("under_score", "*bold* `code` [x](y)", "**under\\_score**: \\*bold\\* \\`code\\` \\[x\\]\\(y\\)")]
    [InlineData("Alpha", "\u0001ACTION waves\u0001", "\\* **Alpha** waves")]
    public void GameLines_AreEscapedForDiscord(string sender, string text, string expected) =>
        Assert.Equal(expected, DiscordChatRelay.ToDiscordLine(sender, text));

    [Theory]
    [InlineData(
        "\u0001ACTION is listening to [https://osu.sundei.eu/b/4622078 Camellia - Parallel Universe Shifter (iLyne) [To Infinity and Beyond]]\u0001",
        "\\* **ThePooN** is listening to [Camellia - Parallel Universe Shifter (iLyne) [To Infinity and Beyond]](https://osu.sundei.eu/b/4622078)")]
    [InlineData(
        "\u0001ACTION is playing [https://osu.sundei.eu/beatmapsets/1#/2 A - B [Hard]] +Hidden\u0001",
        "\\* **ThePooN** is playing [A - B [Hard]](https://osu.sundei.eu/beatmapsets/1#/2) +Hidden")]
    [InlineData("see (this map)[https://osu.ppy.sh/b/1] *wow*", "**ThePooN**: see [this map](https://osu.ppy.sh/b/1) \\*wow\\*")]
    [InlineData("[not a link] and [https://x.test/a]", "**ThePooN**: \\[not a link\\] and \\[https://x.test/a\\]")]
    public void GameLinks_BecomeDiscordLinks(string text, string expected) =>
        Assert.Equal(expected, DiscordChatRelay.ToDiscordLine("ThePooN", text));

    [Theory]
    [InlineData("!link ABC123")]
    [InlineData("  !LINK abc123")]
    public void LinkCodes_AreNeverRelayed(string text) => Assert.Null(DiscordChatRelay.ToDiscordLine("Alpha", text));

    [Fact]
    public void DiscordText_BecomesOneGameLine()
    {
        Assert.Equal("first second third", DiscordChatRelay.ToGameText("first\nsecond\r\n\n third "));
        Assert.Null(DiscordChatRelay.ToGameText(" \n\t "));
        var longText = new string('a', 1000);
        Assert.Equal(DiscordChatRelay.MaxGameMessage, DiscordChatRelay.ToGameText(longText)!.Length);
        Assert.Equal("nice :pog: :dance:", DiscordBotService.PlainEmoji("nice <:pog:123456789012345678> <a:dance:98765432109876543>"));
    }

    [Theory]
    [InlineData("129891", 129891)]
    [InlineData("https://osu.ppy.sh/beatmaps/129891", 129891)]
    [InlineData("https://osu.sundei.eu/beatmapsets/41823#osu/129891", 129891)]
    [InlineData("https://osu.ppy.sh/b/129891?m=0", 129891)]
    [InlineData("https://osu.ppy.sh/beatmapsets/41823", null)]
    [InlineData("nope", null)]
    [InlineData("0", null)]
    public void BeatmapIds_AreReadFromIdsAndLinks(string text, int? expected) => Assert.Equal(expected, DiscordCommands.ParseBeatmapId(text));

    [Theory]
    [InlineData("osu", 0)]
    [InlineData("TAIKO", 1)]
    [InlineData("catch", 2)]
    [InlineData("ctb", 2)]
    [InlineData("mania", 3)]
    [InlineData("chess", null)]
    [InlineData(null, null)]
    public void Rulesets_AreReadFromNames(string? text, int? expected) => Assert.Equal(expected, DiscordFormat.ParseRuleset(text));

    [Fact]
    public void ScoreData_GivesModsWithoutClassic_AndHits()
    {
        var (mods, great, ok, meh, miss) = OsuWebCommunity.ReadData(
            """{"mods":[{"acronym":"HD"},{"acronym":"DT","settings":{}},{"acronym":"CL"}],"statistics":{"great":300,"ok":12,"meh":3,"miss":1}}""");
        Assert.Equal(["HD", "DT"], mods);
        Assert.Equal((300, 12, 3, 1), (great, ok, meh, miss));
        Assert.Empty(OsuWebCommunity.ReadData("not json").Mods);
    }

    [Fact]
    public void ScoreData_GivesTheSpeedPlayed_LazersPickedOneToo()
    {
        Assert.Equal(1.5, OsuWebCommunity.ReadRate("""{"mods":[{"acronym":"HD"},{"acronym":"DT","settings":{}}]}"""));
        Assert.Equal(1.1, OsuWebCommunity.ReadRate("""{"mods":[{"acronym":"DT","settings":{"speed_change":1.1}}]}"""));
        Assert.Equal(0.8, OsuWebCommunity.ReadRate("""{"mods":[{"acronym":"HT","settings":{"speed_change":0.8}}]}"""));
        Assert.Equal(0.75, OsuWebCommunity.ReadRate("""{"mods":["DC"]}"""));
        Assert.Equal(1.0, OsuWebCommunity.ReadRate("""{"mods":[{"acronym":"CL"}]}"""));
        Assert.Null(OsuWebCommunity.ReadRate("""{"mods":[{"acronym":"WU"}]}""")); // changes as it plays
        Assert.Null(OsuWebCommunity.ReadRate("not json"));
    }

    [Fact]
    public void LogSink_KeepsErrors_ButNotTheBotsOwn()
    {
        var sink = new DiscordLogSink();
        sink.CreateLogger("TomYum.Server.Beatmaps").LogError(new InvalidOperationException("boom"), "Import failed for {Set}", 5);
        sink.CreateLogger("TomYum.Server.Beatmaps").LogWarning("just a warning");
        sink.CreateLogger("TomYum.Server.Discord.DiscordBotService").LogError("can't post");

        var error = Assert.Single(sink.Drain(10));
        Assert.Contains("Import failed for 5", error);
        Assert.Contains("InvalidOperationException: boom", error);
        Assert.Empty(sink.Drain(10));
    }

    [Fact]
    public async Task FromDiscord_LinkedPlayers_TalkAsThemselves_WithoutAnEcho()
    {
        var (relay, gateway, users, links, listener, chatLog) = NewRelay();
        var alpha = await users.AddAsync(NewUser("Alpha"));
        await links.LinkAsync(alpha.Id, 1001, DateTime.UtcNow);
        var echoed = new List<ChatOrigin>();
        chatLog.Recorded += (_, origin) => echoed.Add(origin);

        Assert.Equal(RelayOutcome.Relayed, await relay.FromDiscordAsync(1001, "hi from\ndiscord"));
        Assert.Equal(RelayOutcome.NotLinked, await relay.FromDiscordAsync(2002, "who am I"));
        Assert.Equal(RelayOutcome.Empty, await relay.FromDiscordAsync(1001, "!link ABCDEF"));

        var message = Assert.Single(Messages(listener.DequeueAll()));
        Assert.Equal(("Alpha", "hi from discord", "#osu", alpha.Id), message);
        Assert.Equal([ChatOrigin.Discord], echoed);
        Assert.Empty(gateway.Posts); // not sent back to Discord
    }

    [Fact]
    public async Task FromDiscord_SilencedAndRestrictedPlayers_AreMuted()
    {
        var (relay, _, users, links, listener, _) = NewRelay();
        var loud = await users.AddAsync(NewUser("Loud", silenceEnd: DateTimeOffset.UtcNow.AddHours(1)));
        var cheater = await users.AddAsync(NewUser("Cheater", restricted: true));
        await links.LinkAsync(loud.Id, 1, DateTime.UtcNow);
        await links.LinkAsync(cheater.Id, 2, DateTime.UtcNow);

        Assert.Equal(RelayOutcome.Muted, await relay.FromDiscordAsync(1, "let me talk"));
        Assert.Equal(RelayOutcome.Muted, await relay.FromDiscordAsync(2, "let me talk"));
        Assert.Empty(listener.DequeueAll());
    }

    [Fact]
    public async Task GameChat_IsBatchedToDiscord_OnlyForTheRelayedChannel()
    {
        var (relay, gateway, _, _, _, chatLog) = NewRelay();
        await relay.StartAsync(CancellationToken.None);
        try
        {
            chatLog.Record(2, "Alpha", "#osu", "hello");
            chatLog.Record(3, "Web_User", "#osu", "hi from the website", origin: ChatOrigin.Website);
            chatLog.Record(2, "Alpha", "#lobby", "not relayed");
            chatLog.Record(2, "Alpha", "Bravo", "a private message", recipientId: 3);
            chatLog.Record(4, "Linked", "#osu", "came from discord", origin: ChatOrigin.Discord);

            var post = await gateway.WaitForPostAsync();
            Assert.Equal(DiscordFakeGateway.ChatChannel, post.ChannelId);
            Assert.Equal("**Alpha**: hello\n**Web\\_User**: hi from the website", post.Post.Content);
        }
        finally
        {
            await relay.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task BanchoBot_LinkCommand_LinksAndTellsTheBot()
    {
        var codes = new DiscordLinkCodes();
        var links = new InMemoryDiscordLinkRepository();
        var services = new ServiceCollection().AddSingleton<IDiscordLinkRepository>(links).BuildServiceProvider();
        var commands = BanchoBotTestFactory.Commands(services, codes);
        var player = new PlayerRegistry().Add(new BanchoPresence { UserId = 5, Username = "Alpha" });
        (int, ulong)? linked = null;
        codes.Linked += (userId, discordId) =>
        {
            linked = (userId, discordId);
            return Task.CompletedTask;
        };
        var code = codes.Create(777);

        Assert.StartsWith("That code is wrong", await commands.TryHandleAsync(player, "!link WRONG1", BanchoPrivileges.Player));
        Assert.StartsWith("Linked to Discord!", await commands.TryHandleAsync(player, $"!link {code}", BanchoPrivileges.Player));
        Assert.Equal(5, (await links.GetByDiscordIdAsync(777))!.UserId);
        Assert.Equal((5, 777UL), linked);
        Assert.StartsWith("That code is wrong", await commands.TryHandleAsync(player, $"!link {code}", BanchoPrivileges.Player));
    }

    [Fact]
    public async Task BanchoBot_LinkCommand_WithoutABot_SaysSo()
    {
        var commands = BanchoBotTestFactory.Commands(new ServiceCollection().BuildServiceProvider(), discordCodes: null);
        var player = new PlayerRegistry().Add(new BanchoPresence { UserId = 5, Username = "Alpha" });
        Assert.Equal("There's no Discord bot on this server.", await commands.TryHandleAsync(player, "!link ABCDEF", BanchoPrivileges.Player));
    }

    [Fact]
    public async Task LinkAndUnlink_Commands()
    {
        var links = new InMemoryDiscordLinkRepository();
        var codes = new DiscordLinkCodes();
        ulong? unlinked = null;
        codes.Unlinked += id =>
        {
            unlinked = id;
            return Task.CompletedTask;
        };
        var services = new ServiceCollection().AddSingleton<IDiscordLinkRepository>(links).BuildServiceProvider();
        var commands = new DiscordCommands(services.GetRequiredService<IServiceScopeFactory>(), new PlayerRegistry(), codes, new DiscordFormat("https://osu.test", "https://a.test"));

        var link = await commands.RunAsync("link", new Dictionary<string, string>(), 9);
        Assert.True(link.Private);
        Assert.Contains("!link ", link.Post.Content);

        Assert.Equal("Your Discord account isn't linked.", (await commands.RunAsync("unlink", new Dictionary<string, string>(), 9)).Post.Content);
        await links.LinkAsync(3, 9, DateTime.UtcNow);
        Assert.Equal("Unlinked.", (await commands.RunAsync("unlink", new Dictionary<string, string>(), 9)).Post.Content);
        Assert.Equal(9UL, unlinked);

        // Player lookups need the website.
        var profile = await commands.RunAsync("profile", new Dictionary<string, string> { ["user"] = "Alpha" }, 9);
        Assert.Equal("Player lookups need the website (osu-web mode).", profile.Post.Content);
    }

    [Fact]
    public async Task Online_ListsVisiblePlayers()
    {
        var players = new PlayerRegistry();
        players.Add(BanchoBot.CreatePresence());
        players.Add(new BanchoPresence { UserId = 2, Username = "Bravo" });
        players.Add(new BanchoPresence { UserId = 4, Username = "alpha" });
        players.Add(new BanchoPresence { UserId = 5, Username = "Hidden" }).IsRestricted = true;
        var services = new ServiceCollection().AddSingleton<IDiscordLinkRepository>(new InMemoryDiscordLinkRepository()).BuildServiceProvider();
        var commands = new DiscordCommands(services.GetRequiredService<IServiceScopeFactory>(), players, new DiscordLinkCodes(), new DiscordFormat("https://osu.test", "https://a.test"));

        Assert.Equal("**2** online\n▸ **stable** (2): alpha, Bravo", (await commands.RunAsync("online", new Dictionary<string, string>(), 1)).Post.Content);
    }

    [Fact]
    public void MapLinks_ShowArtistTitleMapperAndDifficulty_AndUseTheShortLink()
    {
        var score = new ScoreCard(
            1, 2, "Lilian", 0, 4622078, 2203483, "Camellia", "Parallel Universe Shifter", "To Infinity and Beyond", "iLyne",
            6.66, 4, [], 0.97, 500, 600, "S", true, 200, 900_000, 5_000_000, true, 500, 10, 0, 1, DateTime.UtcNow);

        Assert.Equal(
            "[Camellia - Parallel Universe Shifter (iLyne) [To Infinity and Beyond]](https://osu.sundei.eu/b/4622078)",
            new DiscordFormat("https://osu.sundei.eu", "https://a.sundei.eu").MapLink(score));
    }

    [Theory]
    [InlineData("Artist - Title (TV Size) [Extra]", "Artist - Title (TV Size) [Extra]")]
    [InlineData("under_score*", "under_score*")]
    [InlineData("Weird ] name [x]", "Weird ) name (x)")]
    [InlineData("Open [ only", "Open ( only")]
    public void LinkLabels_AreNotEscaped_AndCantBreakTheLink(string text, string expected)
    {
        Assert.Equal(expected, DiscordFormat.LinkLabel(text));
    }

    private static (DiscordChatRelay, DiscordFakeGateway, InMemoryUserRepository, InMemoryDiscordLinkRepository, OnlinePlayer Listener, ChatLog) NewRelay()
    {
        var users = new InMemoryUserRepository();
        var links = new InMemoryDiscordLinkRepository();
        var services = new ServiceCollection()
            .AddSingleton<IUserRepository>(users)
            .AddSingleton<IDiscordLinkRepository>(links)
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var chatLog = new ChatLog(scopes, TimeProvider.System, NullLogger<ChatLog>.Instance);
        var channels = new ChannelRegistry();
        var listener = new PlayerRegistry().Add(new BanchoPresence { UserId = 50, Username = "Listener" });
        channels.Get("#osu")!.Join(listener);
        var gateway = new DiscordFakeGateway();
        var relay = new DiscordChatRelay(DiscordFakeGateway.Options(), gateway, chatLog, channels, scopes, TimeProvider.System);
        return (relay, gateway, users, links, listener, chatLog);
    }

    private static User NewUser(string name, bool restricted = false, DateTimeOffset? silenceEnd = null) => new()
    {
        Username = name,
        Email = $"{name.ToLowerInvariant()}@tomyum.dev",
        PasswordBcrypt = "x",
        IsRestricted = restricted,
        SilenceEndUtc = silenceEnd,
    };

    private static List<(string Sender, string Text, string Target, int SenderId)> Messages(byte[] body)
    {
        var messages = new List<(string, string, string, int)>();
        var reader = new BanchoPacketReader(body);
        while (reader.CanReadHeader)
        {
            var header = reader.ReadHeader();
            if ((ushort)header.Id == (ushort)ServerPacketId.SendMessage)
                messages.Add((reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadInt32()));
            else
                reader.ReadBytes((int)header.Length);
        }
        return messages;
    }

    private sealed class MovableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

/// <summary>Records what the bot would post; always "connected".</summary>
public sealed class DiscordFakeGateway : IDiscordGateway
{
    public const ulong ChatChannel = 11;
    public const ulong Beatmaps = 12;
    public const ulong PublicLog = 13;
    public const ulong ServerLog = 14;
    public const ulong UserLog = 15;
    public const ulong BnRequest = 16;

    private readonly Lock _lock = new();
    private readonly List<(ulong ChannelId, DiscordPost Post)> _posts = [];

    public bool IsConnected { get; set; } = true;

    public List<(ulong DiscordUserId, IReadOnlySet<ulong> Wanted)> RoleSyncs { get; } = [];

    public IReadOnlyList<(ulong ChannelId, DiscordPost Post)> Posts
    {
        get
        {
            lock (_lock)
                return _posts.ToList();
        }
    }

    public static DiscordOptions Options() => new()
    {
        Token = "test",
        GuildId = 1,
        ChatChannelId = ChatChannel,
        BeatmapsChannelId = Beatmaps,
        PublicLogChannelId = PublicLog,
        ServerLogChannelId = ServerLog,
        UserLogChannelId = UserLog,
        BnRequestChannelId = BnRequest,
        AdminRoleId = 21,
        GmtRoleId = 22,
        NatRoleId = 23,
        BnRoleId = 24,
        BigPlayPp = 300,
    };

    public IReadOnlyList<DiscordPost> In(ulong channelId) => Posts.Where(p => p.ChannelId == channelId).Select(p => p.Post).ToList();

    public void Clear()
    {
        lock (_lock)
            _posts.Clear();
    }

    public Task PostAsync(ulong channelId, DiscordPost post, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            _posts.Add((channelId, post));
        return Task.CompletedTask;
    }

    public Task<bool> SyncRolesAsync(ulong discordUserId, IReadOnlySet<ulong> managed, IReadOnlySet<ulong> wanted, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            RoleSyncs.Add((discordUserId, wanted));
        return Task.FromResult(true);
    }

    private ulong _nextMessage = 1000;

    /// <summary>Messages by id, as last posted or edited; pinned ones; roles given and taken; the bot's status line.</summary>
    public Dictionary<ulong, (ulong ChannelId, DiscordPost Post)> Messages { get; } = [];

    public HashSet<ulong> Pinned { get; } = [];

    public List<(ulong DiscordUserId, ulong RoleId, bool Give)> RoleChanges { get; } = [];

    public string? Activity { get; private set; }

    public Task<ulong?> SendAsync(ulong channelId, DiscordPost post, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _posts.Add((channelId, post));
            var id = _nextMessage++;
            Messages[id] = (channelId, post);
            return Task.FromResult<ulong?>(id);
        }
    }

    public Task<bool> EditAsync(ulong channelId, ulong messageId, DiscordPost post, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!Messages.TryGetValue(messageId, out var message) || message.ChannelId != channelId)
                return Task.FromResult(false);
            Messages[messageId] = (channelId, post);
            return Task.FromResult(true);
        }
    }

    public Task PinAsync(ulong channelId, ulong messageId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            Pinned.Add(messageId);
        return Task.CompletedTask;
    }

    public Task<ulong> EnsureRoleAsync(string name, uint color, CancellationToken cancellationToken = default) => Task.FromResult(777UL);

    public Task<bool> SetRoleAsync(ulong discordUserId, ulong roleId, bool give, CancellationToken cancellationToken = default)
    {
        lock (_lock)
            RoleChanges.Add((discordUserId, roleId, give));
        return Task.FromResult(true);
    }

    public Task SetActivityAsync(string text, CancellationToken cancellationToken = default)
    {
        Activity = text;
        return Task.CompletedTask;
    }

    public async Task<(ulong ChannelId, DiscordPost Post)> WaitForPostAsync()
    {
        for (var i = 0; i < 100; i++)
        {
            if (Posts is [var first, ..])
                return first;
            await Task.Delay(50);
        }
        throw new TimeoutException("Nothing was posted.");
    }
}

/// <summary>How the bot's settings are read.</summary>
public sealed class DiscordOptionsTests
{
    [Fact]
    public void EmptyIds_FromAnEnvFile_MeanNotSet()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Discord:Token"] = "t",
                ["Discord:GuildId"] = "1554036446271373342",
                ["Discord:ChatChannelId"] = "",
                ["Discord:AdminRoleId"] = "1554038080585863258",
            })
            .Build();
        var options = DiscordOptions.Read(configuration.GetSection(DiscordOptions.SectionName));

        Assert.True(options.IsConfigured);
        Assert.Equal(0UL, options.ChatChannelId);
        Assert.Equal([1554038080585863258UL], options.ManagedRoles());
        Assert.Equal("#osu", options.ChatChannel);
        Assert.Equal(300, options.BigPlayPp);
    }

    [Fact]
    public void ABadId_SaysWhichSetting()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Discord:GmtRoleId"] = "@gmt" })
            .Build();
        var error = Assert.Throws<InvalidOperationException>(() => DiscordOptions.Read(configuration.GetSection("Discord")));
        Assert.Contains("Discord:GmtRoleId", error.Message);
    }
}
