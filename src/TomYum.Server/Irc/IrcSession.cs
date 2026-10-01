using System.Globalization;
using System.Net.Sockets;
using System.Text;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Irc;
using TomYum.Server.Bancho;
using TomYum.Server.Bancho.Packets;

namespace TomYum.Server.Irc;

/// <summary>
/// One IRC connection. Once logged in (<c>PASS</c> = the player's IRC password from BanchoBot's
/// <c>!irc</c>, <c>NICK</c> = their name with <c>_</c> for spaces) it's an ordinary online session:
/// what an IRC user says goes through the same handlers as the game's chat packets (channels, PMs,
/// BanchoBot commands, silences, the website/lazer/Discord bridges), and the chat packets queued for
/// the session come out as IRC lines. One session per player: someone in the game can't also be on
/// IRC, and logging into the game ends their IRC session.
/// </summary>
public sealed class IrcSession : IDisposable
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(200);
    private const int MaxTextBytes = 400;

    private readonly TcpClient _client;
    private readonly IrcOptions _options;
    private readonly IrcTokens _tokens;
    private readonly PlayerRegistry _players;
    private readonly ChannelRegistry _channels;
    private readonly MatchRegistry _matches;
    private readonly BanchoPacketDispatcher _dispatcher;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<IrcSession> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly HashSet<string> _joined = new(StringComparer.OrdinalIgnoreCase);
    private StreamWriter? _writer;
    private OnlinePlayer? _player;
    private string _nick = "*";

    public IrcSession(
        TcpClient client, IrcOptions options, IrcTokens tokens, PlayerRegistry players, ChannelRegistry channels,
        MatchRegistry matches, BanchoPacketDispatcher dispatcher, IServiceScopeFactory scopes, ILogger<IrcSession> logger)
    {
        _client = client;
        _options = options;
        _tokens = tokens;
        _players = players;
        _channels = channels;
        _matches = matches;
        _dispatcher = dispatcher;
        _scopes = scopes;
        _logger = logger;
    }

    private string Host => _options.ServerName;

    public void Dispose()
    {
        _writeGate.Dispose();
        _writer?.Dispose();
        _client.Dispose();
    }

    private string Mask(string nick) => $"{nick}!{nick}@{Host}";

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        var client = _client;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            _writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

            if (!await RegisterAsync(reader, cancel.Token).ConfigureAwait(false))
                return;

            var pump = PumpAsync(cancel.Token);
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    var text = await ReadLineAsync(reader, TimeSpan.FromMinutes(5), cancel.Token).ConfigureAwait(false);
                    if (text is null || _players.GetByToken(_player!.Token) is null)
                        break;
                    _player.LastActivityUtc = DateTimeOffset.UtcNow;
                    if (IrcLine.Parse(text) is { } line && !await HandleAsync(line, cancel.Token).ConfigureAwait(false))
                        break;
                }
            }
            finally
            {
                await cancel.CancelAsync().ConfigureAwait(false);
                try
                {
                    await pump.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away.
        }
        finally
        {
            await LogOutAsync().ConfigureAwait(false);
        }
    }

    // ── logging in ────────────────────────────────────────────────────────────────────────

    private async Task<bool> RegisterAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? password = null, nick = null;
        var hasUser = false;
        while (nick is null || !hasUser)
        {
            var text = await ReadLineAsync(reader, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (text is null || IrcLine.Parse(text) is not { } line)
            {
                if (text is null)
                    return false;
                continue;
            }
            switch (line.Command)
            {
                case "PASS":
                    password = line[0];
                    break;
                case "NICK":
                    nick = line[0];
                    break;
                case "USER":
                    hasUser = true;
                    break;
                case "CAP":
                    if (line[0].Equals("LS", StringComparison.OrdinalIgnoreCase))
                        await SendAsync(Host, "CAP", "*", "LS", string.Empty).ConfigureAwait(false);
                    break;
                case "PING":
                    await SendAsync(Host, "PONG", Host, line[0]).ConfigureAwait(false);
                    break;
                case "QUIT":
                    return false;
            }
        }

        using var scope = _scopes.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<IUserRepository>()
            .GetBySafeNameAsync(User.MakeSafeName(nick), cancellationToken).ConfigureAwait(false);
        if (user is null || password is null || !await _tokens.VerifyAsync(user.Id, password).ConfigureAwait(false))
        {
            await SendAsync(Host, "464", nick, _tokens.FromWebsite
                ? $"Bad username or IRC key. Make your IRC key in the website's account settings{(_options.KeyPageUrl is { } page ? ": " + page : ".")}"
                : "Bad username or IRC password. Send !irc to BanchoBot in the game or on the website for your IRC password.").ConfigureAwait(false);
            await ErrorAsync("Bad authentication").ConfigureAwait(false);
            return false;
        }
        if (user.IsDeleted)
        {
            await ErrorAsync("This account has been deleted.").ConfigureAwait(false);
            return false;
        }
        if (user.IsRestricted)
        {
            await ErrorAsync("Your account is restricted; IRC isn't available.").ConfigureAwait(false);
            return false;
        }
        if (scope.ServiceProvider.GetService<TomYum.Server.Admin.Maintenance>() is { IsLocked: true } maintenance
            && !ModerationCommands.IsStaff(user.Privileges))
        {
            await ErrorAsync(maintenance.Notice).ConfigureAwait(false);
            return false;
        }
        if (_players.GetByUserId(user.Id) is { } online && !online.IsIrc)
        {
            await ErrorAsync("You're online in osu!; IRC and the game can't be used at the same time here.").ConfigureAwait(false);
            return false;
        }

        _nick = IrcLine.Nick(user.Username);
        _player = _players.Add(new BanchoPresence
        {
            UserId = user.Id,
            Username = user.Username,
            Privileges = PrivilegeMapper.ToBancho(user.Privileges),
            CountryCode = CountryCodes.ToNumeric(user.Country),
        });
        _player.IsIrc = true;
        _player.SilenceEndUtc = user.SilenceEndUtc;
        _logger.LogInformation("IRC: {Username} (id {UserId}) logged in.", user.Username, user.Id);

        await SendAsync(Host, "001", _nick, $"Welcome to the osu! IRC server on {Host}, {_nick}").ConfigureAwait(false);
        await SendAsync(Host, "002", _nick, $"Your host is {Host}").ConfigureAwait(false);
        await SendAsync(Host, "003", _nick, "This server works like irc.ppy.sh").ConfigureAwait(false);
        await SendAsync(Host, "375", _nick, $"- {Host} Message of the day -").ConfigureAwait(false);
        foreach (var motd in new[]
                 {
                     "- Channels, PMs and BanchoBot work as in the game (try !help).",
                     "- /join #osu to talk with players in the game, on the website and in lazer.",
                     "- Logging into the game ends this session.",
                 })
        {
            await SendAsync(Host, "372", _nick, motd).ConfigureAwait(false);
        }
        await SendAsync(Host, "376", _nick, "End of /MOTD command.").ConfigureAwait(false);
        return true;
    }

    private async Task LogOutAsync()
    {
        if (_player is null || _players.GetByToken(_player.Token) is null)
            return;
        try
        {
            await DispatchAsync(ClientPacketId.Logout, w => w.WriteInt32(0), CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("IRC: {Nick} left.", _nick);
        }
#pragma warning disable CA1031 // leaving must never throw; the reaper takes the session otherwise
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "IRC: logging {Nick} out failed.", _nick);
            _players.Remove(_player.Token);
        }
    }

    // ── IRC → game ────────────────────────────────────────────────────────────────────────

    /// <summary>Handles a command; false ends the session.</summary>
    private async Task<bool> HandleAsync(IrcLine line, CancellationToken cancellationToken)
    {
        switch (line.Command)
        {
            case "PING":
                await SendAsync(Host, "PONG", Host, line[0]).ConfigureAwait(false);
                break;
            case "PONG":
            case "USERHOST":
            case "CAP":
                break;
            case "QUIT":
                return false;
            case "JOIN":
                foreach (var name in line[0].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    await JoinAsync(name, cancellationToken).ConfigureAwait(false);
                break;
            case "PART":
                foreach (var name in line[0].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    await DispatchAsync(ClientPacketId.ChannelPart, w => w.WriteString(name), cancellationToken).ConfigureAwait(false);
                    _joined.Remove(name);
                    await SendAsync(Mask(_nick), "PART", name, "Leaving").ConfigureAwait(false);
                }
                break;
            case "PRIVMSG":
            case "NOTICE" when line[0].StartsWith('#'):
                await SayAsync(line[0], line[1], cancellationToken).ConfigureAwait(false);
                break;
            case "NAMES":
                await NamesAsync(line[0]).ConfigureAwait(false);
                break;
            case "TOPIC":
                await TopicAsync(line[0]).ConfigureAwait(false);
                break;
            case "LIST":
                foreach (var channel in _channels.All)
                    await SendAsync(Host, "322", _nick, channel.Name, channel.MemberCount.ToString(CultureInfo.InvariantCulture), channel.Topic).ConfigureAwait(false);
                await SendAsync(Host, "323", _nick, "End of /LIST").ConfigureAwait(false);
                break;
            case "MODE":
                if (line[0].StartsWith('#'))
                    await SendAsync(Host, "324", _nick, line[0], "+nt").ConfigureAwait(false);
                break;
            case "WHO":
                await SendAsync(Host, "315", _nick, line[0], "End of /WHO list.").ConfigureAwait(false);
                break;
            case "WHOIS":
                await WhoisAsync(line[0]).ConfigureAwait(false);
                break;
            case "AWAY":
                // Like the game's /away: PMs get answered with it.
                _player!.AwayMessage = line.Parameters is [{ Length: > 0 } away, ..] ? away : null;
                await (_player.AwayMessage is null
                    ? SendAsync(Host, "305", _nick, "You are no longer marked as being away")
                    : SendAsync(Host, "306", _nick, "You have been marked as being away")).ConfigureAwait(false);
                break;
            case "ISON":
                var online = line.Parameters.SelectMany(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Where(nick => _players.GetByUsername(nick.Replace('_', ' ')) is not null || _players.GetByUsername(nick) is not null);
                await SendAsync(Host, "303", _nick, string.Join(' ', online)).ConfigureAwait(false);
                break;
            default:
                await SendAsync(Host, "421", _nick, line.Command, "Unknown command").ConfigureAwait(false);
                break;
        }
        return true;
    }

    private async Task JoinAsync(string name, CancellationToken cancellationToken)
    {
        if (_channels.Get(name) is null)
        {
            await SendAsync(Host, "403", _nick, name, "No such channel").ConfigureAwait(false);
            return;
        }
        await DispatchAsync(ClientPacketId.ChannelJoin, w => w.WriteString(name), cancellationToken).ConfigureAwait(false);
        if (_channels.Get(name) is { } channel && channel.Contains(_player!.UserId) && _joined.Add(channel.Name))
            await AnnounceJoinAsync(channel).ConfigureAwait(false);
    }

    private async Task AnnounceJoinAsync(ChatChannel channel)
    {
        await SendAsync(Mask(_nick), "JOIN", channel.Name).ConfigureAwait(false);
        await TopicAsync(channel.Name).ConfigureAwait(false);
        await NamesAsync(channel.Name).ConfigureAwait(false);
    }

    private async Task SayAsync(string target, string text, CancellationToken cancellationToken)
    {
        if (target.Length == 0 || text.Length == 0)
            return;
        if (target.StartsWith('#'))
        {
            await DispatchAsync(ClientPacketId.SendPublicMessage, w => w.WriteString(string.Empty).WriteString(text).WriteString(target).WriteInt32(0), cancellationToken).ConfigureAwait(false);
            return;
        }
        // A nick has underscores for spaces; the player may be called either way.
        var name = _players.GetByUsername(target)?.Presence.Username
                   ?? _players.GetByUsername(target.Replace('_', ' '))?.Presence.Username
                   ?? target.Replace('_', ' ');
        await DispatchAsync(ClientPacketId.SendPrivateMessage, w => w.WriteString(string.Empty).WriteString(text).WriteString(name).WriteInt32(0), cancellationToken).ConfigureAwait(false);
    }

    private async Task NamesAsync(string name)
    {
        if (_channels.Get(name) is { } channel)
        {
            var nicks = channel.Members.Where(m => !m.IsRestricted).Select(m => IrcLine.Nick(m.Presence.Username)).ToList();
            foreach (var chunk in nicks.Chunk(40))
                await SendAsync(Host, "353", _nick, "=", channel.Name, string.Join(' ', chunk)).ConfigureAwait(false);
        }
        await SendAsync(Host, "366", _nick, name, "End of /NAMES list.").ConfigureAwait(false);
    }

    private async Task TopicAsync(string name)
    {
        if (_channels.Get(name) is { } channel)
            await SendAsync(Host, "332", _nick, channel.Name, channel.Topic).ConfigureAwait(false);
    }

    private async Task WhoisAsync(string nick)
    {
        var player = _players.GetByUsername(nick) ?? _players.GetByUsername(nick.Replace('_', ' '));
        if (player is null || player.IsRestricted)
        {
            await SendAsync(Host, "401", _nick, nick, "No such nick").ConfigureAwait(false);
        }
        else
        {
            var who = IrcLine.Nick(player.Presence.Username);
            await SendAsync(Host, "311", _nick, who, who, Host, "*", $"https://osu.{Host.Split('.', 2).ElementAtOrDefault(1)}/users/{player.UserId.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        }
        await SendAsync(Host, "318", _nick, nick, "End of /WHOIS list.").ConfigureAwait(false);
    }

    /// <summary>Runs a client packet through the game's handlers, as if the game had sent it.</summary>
    private async Task DispatchAsync(ClientPacketId id, Action<BanchoPacketWriter> payload, CancellationToken cancellationToken)
    {
        var packet = new BanchoPacketWriter(128).WritePacket((ServerPacketId)(ushort)id, payload).ToArray();
        using var scope = _scopes.CreateScope();
        var reply = await _dispatcher.DispatchAsync(_player!, _players, _channels, _matches, scope.ServiceProvider, packet).ConfigureAwait(false);
        if (reply.Length > 0)
            await TranslateAsync(reply).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    // ── game → IRC ────────────────────────────────────────────────────────────────────────

    /// <summary>Turns what's queued for the session into IRC lines, and keeps the session alive.</summary>
    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(PumpInterval, cancellationToken).ConfigureAwait(false);
            if (_players.GetByToken(_player!.Token) is null)
            {
                await ErrorAsync("You logged in from osu!; this IRC session has ended.").ConfigureAwait(false);
                _client.Close();
                return;
            }
            _player.LastActivityUtc = DateTimeOffset.UtcNow;
            var queued = _player.DequeueAll();
            if (queued.Length > 0)
                await TranslateAsync(queued).ConfigureAwait(false);
        }
    }

    private async Task TranslateAsync(byte[] packets)
    {
        var messages = new List<(ServerPacketId Id, string[] Text)>();
        var reader = new BanchoPacketReader(packets);
        while (reader.CanReadHeader)
        {
            var header = reader.ReadHeader();
            if (reader.Remaining < header.Length)
                break;
            var payload = new BanchoPacketReader(reader.ReadBytes((int)header.Length));
            switch ((ServerPacketId)(ushort)header.Id)
            {
                case ServerPacketId.SendMessage:
                    messages.Add((ServerPacketId.SendMessage, [payload.ReadString(), payload.ReadString(), payload.ReadString()]));
                    break;
                case ServerPacketId.Notification:
                    messages.Add((ServerPacketId.Notification, [payload.ReadString()]));
                    break;
                case ServerPacketId.ChannelJoinSuccess:
                    messages.Add((ServerPacketId.ChannelJoinSuccess, [payload.ReadString()]));
                    break;
                case ServerPacketId.ChannelKick:
                    messages.Add((ServerPacketId.ChannelKick, [payload.ReadString()]));
                    break;
                case ServerPacketId.UserDmBlocked or ServerPacketId.TargetIsSilenced:
                    _ = payload.ReadString();
                    _ = payload.ReadString();
                    messages.Add(((ServerPacketId)(ushort)header.Id, [payload.ReadString()]));
                    break;
            }
        }

        foreach (var (id, text) in messages)
        {
            switch (id)
            {
                case ServerPacketId.SendMessage:
                    var (sender, body, target) = (text[0], text[1], text[2]);
                    if (string.Equals(sender, _player!.Presence.Username, StringComparison.Ordinal))
                        continue;
                    var to = target.StartsWith('#') ? target : _nick;
                    foreach (var part in Split(body))
                        await SendAsync(Mask(IrcLine.Nick(sender)), "PRIVMSG", to, part).ConfigureAwait(false);
                    break;
                case ServerPacketId.Notification:
                    foreach (var part in Split(text[0].ReplaceLineEndings(" ")))
                        await SendAsync(Mask(BanchoBot.Name), "NOTICE", _nick, part).ConfigureAwait(false);
                    break;
                case ServerPacketId.ChannelJoinSuccess:
                    if (_channels.Get(text[0]) is { } channel && _joined.Add(channel.Name))
                        await AnnounceJoinAsync(channel).ConfigureAwait(false);
                    break;
                case ServerPacketId.UserDmBlocked:
                    await SendAsync(Host, "404", _nick, IrcLine.Nick(text[0]), "They only take messages from friends").ConfigureAwait(false);
                    break;
                case ServerPacketId.TargetIsSilenced:
                    await SendAsync(Host, "404", _nick, IrcLine.Nick(text[0]), "They're silenced and can't answer").ConfigureAwait(false);
                    break;
                case ServerPacketId.ChannelKick:
                    if (_joined.Remove(text[0]))
                        await SendAsync(Mask(_nick), "PART", text[0], "Removed from the channel").ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>A long message in pieces that fit an IRC line.</summary>
    private static IEnumerable<string> Split(string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes <= MaxTextBytes)
        {
            yield return text;
            yield break;
        }
        var piece = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Encoding.UTF8.GetByteCount(piece.ToString()) + rune.Utf8SequenceLength > MaxTextBytes)
            {
                yield return piece.ToString();
                piece.Clear();
            }
            piece.Append(rune.ToString());
        }
        if (piece.Length > 0)
            yield return piece.ToString();
    }

    // ── the wire ──────────────────────────────────────────────────────────────────────────

    private static async Task<string?> ReadLineAsync(StreamReader reader, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        try
        {
            var line = await reader.ReadLineAsync(wait.Token).ConfigureAwait(false);
            return line is { Length: > IrcLine.MaxLength } ? line[..IrcLine.MaxLength] : line;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // idle too long
        }
    }

    private Task ErrorAsync(string reason) => SendRawAsync($"ERROR :{reason}");

    private Task SendAsync(string prefix, string command, params string[] parameters) =>
        SendRawAsync(new IrcLine(prefix, command, parameters).Format());

    private async Task SendRawAsync(string line)
    {
        if (_writer is null)
            return;
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(line.ReplaceLineEndings(" ")).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
