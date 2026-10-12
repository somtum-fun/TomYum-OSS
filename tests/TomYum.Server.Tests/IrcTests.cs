using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Irc;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;
using TomYum.Server.Beatmaps;
using TomYum.Server.Irc;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>IRC: the line format, and a real connection chatting with a game player.</summary>
public sealed class IrcTests
{
    [Theory]
    [InlineData(":nick!user@host PRIVMSG #osu :hello there", "nick!user@host", "PRIVMSG", "#osu|hello there")]
    [InlineData("PING :irc.test", null, "PING", "irc.test")]
    [InlineData("user guest 0 * :Real Name", null, "USER", "guest|0|*|Real Name")]
    [InlineData("JOIN #osu,#english", null, "JOIN", "#osu,#english")]
    public void Lines_AreRead(string text, string? prefix, string command, string parameters)
    {
        var line = IrcLine.Parse(text)!;
        Assert.Equal((prefix, command), (line.Prefix, line.Command));
        Assert.Equal(parameters.Split('|'), line.Parameters);
    }

    [Fact]
    public void Lines_AreWritten_WithTheLastParameterTrailing()
    {
        Assert.Equal(":irc.test 001 Some_Player :Welcome in", new IrcLine("irc.test", "001", ["Some_Player", "Welcome in"]).Format());
        Assert.Equal("Some_Player", IrcLine.Nick("Some Player"));
    }

    [Fact]
    public async Task AnIrcUser_ChatsWithGamePlayers_AndBanchoBot()
    {
        await using var server = await IrcHarness.StartAsync();
        var game = server.GamePlayer("Game Player");
        server.Channels.Get("#osu")!.Join(game);

        using var irc = await server.ConnectAsync("Some_Player", await server.Tokens.CreateAsync(server.Ids["Some Player"]));
        Assert.Contains(await irc.UntilAsync(" 376 "), l => l.Contains(" 001 Some_Player ", StringComparison.Ordinal));

        await irc.SendAsync("JOIN #osu");
        var joined = await irc.UntilAsync(" 366 ");
        Assert.Contains(joined, l => l == ":Some_Player!Some_Player@irc.test JOIN :#osu");
        Assert.Contains(joined, l => l.Contains(" 353 ", StringComparison.Ordinal) && l.Contains("Game_Player", StringComparison.Ordinal));

        // IRC → game: channel and PM.
        await irc.SendAsync("PRIVMSG #osu :hello from irc");
        await irc.SendAsync("PRIVMSG Game_Player :psst");
        var toGame = await IrcHarness.WaitForMessagesAsync(game, 2);
        Assert.Contains(("Some Player", "hello from irc", "#osu"), toGame);
        Assert.Contains(("Some Player", "psst", "Game Player"), toGame);

        // Game → IRC.
        await server.SayAsync(game, "#osu", "hi irc");
        Assert.Contains(":Game_Player!Game_Player@irc.test PRIVMSG #osu :hi irc", await irc.UntilAsync("hi irc"));

        // BanchoBot answers IRC like the game.
        await irc.SendAsync("PRIVMSG BanchoBot :!ping");
        Assert.Contains(":BanchoBot!BanchoBot@irc.test PRIVMSG Some_Player :pong!", await irc.UntilAsync("pong!"));

        // Logging into the game ends the IRC session.
        server.GamePlayer("Some Player");
        Assert.Contains(await irc.UntilAsync("ERROR"), l => l.StartsWith("ERROR :You logged in from osu!", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASecondIrcLogin_LogsTheFirstOut_ChannelsAndAll()
    {
        await using var server = await IrcHarness.StartAsync();
        var password = await server.Tokens.CreateAsync(server.Ids["Some Player"]);
        using var first = await server.ConnectAsync("Some_Player", password);
        await first.UntilAsync(" 376 ");
        await first.SendAsync("JOIN #osu");
        await first.UntilAsync(" 366 ");
        var old = server.Players.GetByUserId(server.Ids["Some Player"])!;
        Assert.Contains(old, server.Channels.Get("#osu")!.Members);

        using var second = await server.ConnectAsync("Some_Player", password);
        await second.UntilAsync(" 376 ");

        Assert.DoesNotContain(old, server.Channels.Get("#osu")!.Members);
        Assert.Null(server.Players.GetByToken(old.Token));
        Assert.Contains(await first.UntilAsync("ERROR"), l => l.StartsWith("ERROR :", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWrongPassword_IsRefused_AndSoIsSomeoneInTheGame()
    {
        await using var server = await IrcHarness.StartAsync();
        await server.Tokens.CreateAsync(server.Ids["Some Player"]);
        using (var wrong = await server.ConnectAsync("Some_Player", "nope"))
            Assert.Contains(await wrong.UntilAsync("ERROR"), l => l.Contains(" 464 ", StringComparison.Ordinal));

        server.GamePlayer("Game Player");
        using var inGame = await server.ConnectAsync("Game_Player", await server.Tokens.CreateAsync(server.Ids["Game Player"]));
        Assert.Contains(await inGame.UntilAsync("ERROR"), l => l.Contains("online in osu!", StringComparison.Ordinal));
    }

    private sealed class IrcHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly IrcServer _server;
        private readonly int _port;

        private IrcHarness(ServiceProvider services, IrcServer server, int port)
        {
            _services = services;
            _server = server;
            _port = port;
        }

        /// <summary>The test accounts' ids by name.</summary>
        public Dictionary<string, int> Ids { get; private init; } = new Dictionary<string, int>();

        public PlayerRegistry Players => _services.GetRequiredService<PlayerRegistry>();

        public ChannelRegistry Channels => _services.GetRequiredService<ChannelRegistry>();

        public IrcTokens Tokens => _services.GetRequiredService<IrcTokens>();

        public static async Task<IrcHarness> StartAsync()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var users = new InMemoryUserRepository();
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var name in new[] { "Some Player", "Game Player" })
                ids[name] = (await users.AddAsync(new User { Username = name, SafeName = User.MakeSafeName(name), Email = $"{name[0]}@x.dev", PasswordBcrypt = "x" })).Id;

            var services = new ServiceCollection()
                .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
                .AddSingleton<IUserRepository>(users)
                .AddSingleton<PlayerRegistry>()
                .AddSingleton<ChannelRegistry>()
                .AddSingleton<MatchRegistry>()
                .AddSingleton(new IrcTokens())
                .AddSingleton(new IrcOptions { Port = port, ServerName = "irc.test" })
                .AddSingleton(sp => new BanchoBot(new BanchoBotCommands(
                    new TomYumVersion(), sp.GetRequiredService<PlayerRegistry>(), sp.GetRequiredService<ChannelRegistry>(), sp.GetRequiredService<MatchRegistry>(),
                    new BeatmapService(new NoMaps()), sp.GetRequiredService<IServiceScopeFactory>(), new Plugins.PluginCommandRegistry(), new Plugins.LoadedPluginRegistry())))
                .AddSingleton<IClientPacketHandler, ChannelJoinHandler>()
                .AddSingleton<IClientPacketHandler, ChannelPartHandler>()
                .AddSingleton<IClientPacketHandler, SendPublicMessageHandler>()
                .AddSingleton<IClientPacketHandler, SendPrivateMessageHandler>()
                .AddSingleton<IClientPacketHandler, LogoutHandler>()
                .AddSingleton<BanchoPacketDispatcher>()
                .BuildServiceProvider();
            var server = ActivatorUtilities.CreateInstance<IrcServer>(services);
            await server.StartAsync(CancellationToken.None);
            return new IrcHarness(services, server, port) { Ids = ids };
        }

        public OnlinePlayer GamePlayer(string name) => Players.Add(new BanchoPresence { UserId = Ids[name], Username = name });

        public async Task<IrcClient> ConnectAsync(string nick, string password)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, _port);
                    var irc = new IrcClient(client);
                    await irc.SendAsync($"PASS {password}");
                    await irc.SendAsync($"NICK {nick}");
                    await irc.SendAsync("USER bot 0 * :bot");
                    return irc;
                }
                catch (SocketException) when (attempt < 20)
                {
                    await Task.Delay(50);
                }
            }
        }

        /// <summary>A game player's chat, as the game client would get it.</summary>
        public static async Task<List<(string Sender, string Text, string Target)>> WaitForMessagesAsync(OnlinePlayer player, int count)
        {
            var messages = new List<(string, string, string)>();
            for (var i = 0; i < 100 && messages.Count < count; i++)
            {
                messages.AddRange(Decode(player.DequeueAll()));
                await Task.Delay(50);
            }
            return messages;
        }

        public async Task SayAsync(OnlinePlayer player, string channel, string text)
        {
            var packet = new BanchoPacketWriter(64).WritePacket((ServerPacketId)(ushort)ClientPacketId.SendPublicMessage,
                w => w.WriteString(string.Empty).WriteString(text).WriteString(channel).WriteInt32(0)).ToArray();
            await _services.GetRequiredService<BanchoPacketDispatcher>().DispatchAsync(player, Players, Channels, _services.GetRequiredService<MatchRegistry>(), _services, packet);
        }

        private static List<(string, string, string)> Decode(byte[] bytes)
        {
            var result = new List<(string, string, string)>();
            var reader = new BanchoPacketReader(bytes);
            while (reader.CanReadHeader)
            {
                var header = reader.ReadHeader();
                var payload = new BanchoPacketReader(reader.ReadBytes((int)header.Length));
                if ((ServerPacketId)(ushort)header.Id == ServerPacketId.SendMessage)
                    result.Add((payload.ReadString(), payload.ReadString(), payload.ReadString()));
            }
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            await _server.StopAsync(CancellationToken.None);
            _server.Dispose();
            await _services.DisposeAsync();
        }
    }

    private sealed class IrcClient : IDisposable
    {
        private readonly TcpClient _client;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        public IrcClient(TcpClient client)
        {
            _client = client;
            _reader = new StreamReader(client.GetStream(), Encoding.UTF8);
            _writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
        }

        public Task SendAsync(string line) => _writer.WriteLineAsync(line);

        /// <summary>Lines up to and including the first containing <paramref name="marker"/>.</summary>
        public async Task<List<string>> UntilAsync(string marker)
        {
            var lines = new List<string>();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await _reader.ReadLineAsync(timeout.Token) is { } line)
            {
                lines.Add(line);
                if (line.Contains(marker, StringComparison.Ordinal))
                    return lines;
            }
            return lines;
        }

        public void Dispose()
        {
            _reader.Dispose();
            _writer.Dispose();
            _client.Dispose();
        }
    }

    private sealed class NoMaps : IOsuApiClient
    {
        public Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default) => Task.FromResult<Beatmap?>(null);

        public Task<IReadOnlyList<Beatmap>> GetSetAsync(int setId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public Task<IReadOnlyList<Beatmap>> GetSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Beatmap>>([]);

        public bool SupportsSince => false;
    }
}
