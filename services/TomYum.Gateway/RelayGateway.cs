using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace TomYum.Gateway;

/// <summary>
/// Carries each connection to the main server over a WebSocket (<c>/_gateway/&lt;name&gt;</c>), where
/// it's an ordinary session of its kind. The bytes pass through as they are.
/// </summary>
public abstract class RelayGateway : TcpGatewayService
{
    private readonly Upstream _upstream;

    protected RelayGateway(Upstream upstream, ConnectionLimiter limiter, ILogger logger)
        : base(limiter, logger)
    {
        _upstream = upstream;
    }

    /// <summary>The main server's path for these connections (<c>_gateway/bancho</c>, <c>_gateway/irc</c>).</summary>
    protected abstract string Path { get; }

    /// <summary>What the client is told when the main server can't be reached, or null to just close.</summary>
    protected virtual byte[]? Unreachable => null;

    protected override async Task HandleAsync(TcpClient client, IPAddress address, CancellationToken cancellationToken)
    {
        var tcp = client.GetStream();
        ClientWebSocket socket;
        try
        {
            socket = await _upstream.ConnectAsync(Path, address, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            Logger.LogWarning(ex, "{Name}: couldn't reach the main server for {Address}.", Name, address);
            if (Unreachable is { } notice)
                await tcp.WriteAsync(notice, cancellationToken).ConfigureAwait(false);
            return;
        }

        Logger.LogInformation("{Name}: {Address} connected.", Name, address);
        using (socket)
        {
            await using var relay = WebSocketStream.Create(socket, WebSocketMessageType.Binary, ownsWebSocket: false);
            using var ended = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var up = tcp.CopyToAsync(relay, ended.Token);
            var down = relay.CopyToAsync(tcp, ended.Token);
            await Task.WhenAny(up, down).ConfigureAwait(false);
            await ended.CancelAsync().ConfigureAwait(false);
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
                {
                }
            }
            try
            {
                await Task.WhenAll(up, down).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
        Logger.LogInformation("{Name}: {Address} disconnected.", Name, address);
    }
}

/// <summary>
/// Bancho over TCP for builds before HTTP bancho (pre-2014; <c>server.&lt;domain&gt;:13381</c>, like
/// <c>server.titanic.sh</c>), run by the main server's TCP bancho: login, packets pushed as they're
/// queued, pings while idle, a dropped connection logs out.
/// </summary>
public sealed class BanchoGateway : RelayGateway
{
    private readonly GatewayOptions _options;

    public BanchoGateway(GatewayOptions options, Upstream upstream, ConnectionLimiter limiter, ILogger<BanchoGateway> logger)
        : base(upstream, limiter, logger) => _options = options;

    protected override string Name => "Bancho (TCP)";

    protected override IReadOnlyList<int> Ports => _options.Bancho.Ports;

    protected override string Path => "_gateway/bancho";
}

/// <summary>IRC (<c>irc.&lt;domain&gt;:6667</c>), run by the main server's IRC.</summary>
public sealed class IrcGateway : RelayGateway
{
    private readonly GatewayOptions _options;

    public IrcGateway(GatewayOptions options, Upstream upstream, ConnectionLimiter limiter, ILogger<IrcGateway> logger)
        : base(upstream, limiter, logger) => _options = options;

    protected override string Name => "IRC";

    protected override IReadOnlyList<int> Ports => [_options.Irc.Port];

    protected override string Path => "_gateway/irc";

    protected override byte[]? Unreachable => ":gateway NOTICE * :The chat server can't be reached right now. Try again later.\r\n"u8.ToArray();
}
