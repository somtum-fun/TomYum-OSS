using System.Net;
using System.Net.Sockets;
using System.Text;
using TomYum.Bancho;
using TomYum.Server.Bancho.Packets;

namespace TomYum.Server.Bancho;

/// <summary>Settings for bancho over raw TCP (<c>Bancho:Tcp</c>), what osu! builds up to b20130815 speak.</summary>
public sealed class TcpBanchoOptions
{
    public const string Section = "Bancho:Tcp";

    /// <summary>Off by default: TCP needs a public address for the ports (docs/old-clients.md: a relay).</summary>
    public bool Enabled { get; set; }

    /// <summary>The ports old clients connect to (osu!'s were 13380 to 13384).</summary>
    public int[] Ports { get; set; } = [13380, 13381, 13382, 13383, 13384];

    /// <summary>Connections start with a PROXY protocol header (a relay such as frp): the player's real address.</summary>
    public bool ProxyProtocol { get; set; }
}

/// <summary>
/// Bancho over raw TCP for old osu! builds (docs/old-clients.md): they connect, send the same
/// three-line login as HTTP bancho's body, then exchange the same packets on the open connection.
/// Sessions are the same as HTTP ones (login handler, dispatcher, <see cref="BanchoExchange"/>); the
/// server pushes what's queued for the player instead of waiting for a poll.
/// </summary>
public sealed class TcpBanchoServer : BackgroundService
{
    private readonly TcpBanchoOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<TcpBanchoServer> _logger;

    public TcpBanchoServer(TcpBanchoOptions options, IServiceProvider services, ILogger<TcpBanchoServer> logger)
    {
        _options = options;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listeners = new List<TcpListener>();
        foreach (var port in _options.Ports.Distinct())
        {
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start();
                listeners.Add(listener);
            }
            catch (SocketException ex)
            {
                _logger.LogError(ex, "TCP bancho: can't listen on port {Port}.", port);
            }
        }
        if (listeners.Count == 0)
            return;
        _logger.LogInformation("TCP bancho: listening on port(s) {Ports}{Proxy}.",
            string.Join(", ", listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port)), _options.ProxyProtocol ? " (PROXY protocol)" : string.Empty);

        try
        {
            await Task.WhenAll(listeners.Select(l => AcceptAsync(l, stoppingToken))).ConfigureAwait(false);
        }
        finally
        {
            foreach (var listener in listeners)
                listener.Stop();
        }
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                var session = new TcpBanchoSession(client, _options.ProxyProtocol, _services, _logger);
                _ = Task.Run(async () =>
                {
                    using (session)
                        await session.RunAsync(stoppingToken).ConfigureAwait(false);
                }, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>
/// One TCP bancho connection: PROXY header (optional), login, then packets both ways. The connection
/// is a TCP socket on the bancho ports, or one relayed by TomYum.Gateway (docs/gateway.md).
/// </summary>
internal sealed class TcpBanchoSession : IDisposable
{
    private const int MaxLoginBytes = 4096;
    private const int MaxPacketBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// A ping goes out after this long without anything sent: old builds reconnect when bancho says
    /// nothing for a while (b1122 after 30 seconds), as bancho then pinged them.
    /// </summary>
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(10);

    private static readonly byte[] PingPacket = new BanchoPacketWriter(8).WritePacket(ServerPacketId.Pong, null).ToArray();

    private readonly TcpClient? _client;
    private readonly bool _proxyProtocol;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Stream? _stream;
    private IPAddress? _address;
    private long _lastWriteTicks = Environment.TickCount64;

    public TcpBanchoSession(TcpClient client, bool proxyProtocol, IServiceProvider services, ILogger logger)
    {
        _client = client;
        _proxyProtocol = proxyProtocol;
        _services = services;
        _logger = logger;
    }

    /// <summary>A connection relayed by the gateway: its stream, and the player's address as the gateway saw it.</summary>
    public TcpBanchoSession(Stream stream, IPAddress? address, IServiceProvider services, ILogger logger)
    {
        _stream = stream;
        _address = address;
        _services = services;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (_client is not null)
        {
            _client.NoDelay = true;
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            _stream = _client.GetStream();
            _address = (_client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        }
        if (_stream is null)
            return;
        var address = _address;
        var buffer = new List<byte>(512);
        var chunk = new byte[16 * 1024];

        // Up to the login (and the PROXY header before it): within LoginTimeout.
        string? loginBody = null;
        using (var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            loginTimeout.CancelAfter(LoginTimeout);
            var headerRead = !_proxyProtocol;
            try
            {
                while (loginBody is null)
                {
                    var read = await _stream.ReadAsync(chunk, loginTimeout.Token).ConfigureAwait(false);
                    if (read == 0)
                        return;
                    buffer.AddRange(chunk.AsSpan(0, read));
                    if (!headerRead)
                    {
                        var result = ProxyProtocol.TryRead(buffer.ToArray(), out var consumed, out var proxied);
                        if (result == ProxyProtocol.Result.NeedMore)
                            continue;
                        if (result == ProxyProtocol.Result.Invalid)
                            return; // the relay always sends one; anything else isn't the relay
                        address = proxied ?? address;
                        buffer.RemoveRange(0, consumed);
                        headerRead = true;
                    }
                    if (buffer.Count > MaxLoginBytes)
                        return;
                    loginBody = LoginBody(buffer);
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                return; // no login in time
            }
        }

        var ip = address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4().ToString() : address?.ToString();
        OnlinePlayer? player;
        string? token;
        using (var scope = _services.CreateScope())
        {
            var country = scope.ServiceProvider.GetService<IpCountry>()?.Lookup(ip) ?? string.Empty;
            var outcome = await scope.ServiceProvider.GetRequiredService<BanchoLoginHandler>()
                .HandleAsync(loginBody, country, stoppingToken, ip).ConfigureAwait(false);
            token = outcome.Token;
            var registry = scope.ServiceProvider.GetRequiredService<PlayerRegistry>();
            player = token is null ? null : registry.GetByToken(token);
            if (player is not null && scope.ServiceProvider.GetService<LoginIps>() is { } ips)
                await ips.RecordAsync(player.UserId, ip).ConfigureAwait(false);
            await WriteAsync(BanchoExchange.LoginReply(outcome.Body, loginBody, player, scope.ServiceProvider), stoppingToken).ConfigureAwait(false);
        }
        if (player is null || token is null)
            return; // refused: the reply says why

        buffer.Clear();
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var push = PushAsync(player, token, connection.Token);
        try
        {
            while (!connection.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(chunk, connection.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                buffer.AddRange(chunk.AsSpan(0, read));
                var packets = TakePackets(buffer);
                if (packets is null)
                    break; // a packet too large to be one
                if (packets.Length > 0)
                    await ExchangeAsync(player, packets, connection.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            await connection.CancelAsync().ConfigureAwait(false);
            try
            {
                await push.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            await LogoutAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>The login as HTTP bancho's body ("name\npassword\nclient info\n"), once three lines are in.</summary>
    internal static string? LoginBody(List<byte> buffer)
    {
        var text = Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r", string.Empty, StringComparison.Ordinal);
        var lines = text.Split('\n');
        return lines.Length < 4 ? null : $"{lines[0]}\n{lines[1]}\n{lines[2]}\n";
    }

    /// <summary>The complete packets at the front of <paramref name="buffer"/> (taken out of it); null when one claims an absurd length.</summary>
    internal static byte[]? TakePackets(List<byte> buffer)
    {
        var offset = 0;
        while (offset + 7 <= buffer.Count)
        {
            var length = buffer[offset + 3] | (buffer[offset + 4] << 8) | (buffer[offset + 5] << 16) | (buffer[offset + 6] << 24);
            if (length < 0 || length > MaxPacketBytes)
                return null;
            if (offset + 7 + length > buffer.Count)
                break;
            offset += 7 + length;
        }
        var packets = buffer.GetRange(0, offset).ToArray();
        buffer.RemoveRange(0, offset);
        return packets;
    }

    private async Task ExchangeAsync(OnlinePlayer player, byte[] packets, CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var response = await BanchoExchange.RunAsync(player, packets,
            sp.GetRequiredService<BanchoPacketDispatcher>(), sp.GetRequiredService<PlayerRegistry>(),
            sp.GetRequiredService<ChannelRegistry>(), sp.GetRequiredService<MatchRegistry>(), sp).ConfigureAwait(false);
        await WriteAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends what's queued for the player as it comes (HTTP clients poll for it); ends when the session does.</summary>
    private async Task PushAsync(OnlinePlayer player, string token, CancellationToken cancellationToken)
    {
        var registry = _services.GetRequiredService<PlayerRegistry>();
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(PushInterval, cancellationToken).ConfigureAwait(false);
            if (registry.GetByToken(token) is null)
            {
                // Logged out, replaced by a new login or kicked: the connection ends with the session.
                Close();
                return;
            }
            player.LastActivityUtc = DateTimeOffset.UtcNow; // an open connection is a live player
            try
            {
                if (player.HasQueued)
                    await ExchangeAsync(player, [], cancellationToken).ConfigureAwait(false);
                else if (Environment.TickCount64 - Interlocked.Read(ref _lastWriteTicks) >= (long)KeepAlive.TotalMilliseconds)
                    await WriteAsync(PingPacket, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                Close();
                return;
            }
        }
    }

    private async Task WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (data.Length == 0 || _stream is null)
            return;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastWriteTicks, Environment.TickCount64);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Ends the connection (the read loop then ends the session).</summary>
    private void Close()
    {
        if (_client is not null)
            _client.Close();
        else
            _stream?.Dispose();
    }

    /// <summary>A dropped connection logs the player out, as a LOGOUT packet would (unless the session already ended).</summary>
    private async Task LogoutAsync(string token)
    {
        try
        {
            using var scope = _services.CreateScope();
            var sp = scope.ServiceProvider;
            var registry = sp.GetRequiredService<PlayerRegistry>();
            if (registry.GetByToken(token) is not { } player)
                return;
            var logout = new BanchoPacketWriter(16).WritePacket((ServerPacketId)ClientPacketId.Logout, w => w.WriteInt32(0)).ToArray();
            await sp.GetRequiredService<BanchoPacketDispatcher>().DispatchAsync(player, registry,
                sp.GetRequiredService<ChannelRegistry>(), sp.GetRequiredService<MatchRegistry>(), sp, logout).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // the connection is gone either way
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "TCP bancho: logging out a closed connection failed.");
        }
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _client?.Dispose();
    }
}
