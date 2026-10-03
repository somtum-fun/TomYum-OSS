using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace TomYum.Gateway;

/// <summary>
/// IRC (<c>irc.&lt;domain&gt;:6667</c>): each connection is carried to the main server over a
/// WebSocket (<c>/_gateway/irc</c>), where it's an ordinary IRC session. The bytes pass through as they are.
/// </summary>
public sealed class IrcGateway : TcpGatewayService
{
    private readonly GatewayOptions _options;
    private readonly Upstream _upstream;

    public IrcGateway(GatewayOptions options, Upstream upstream, ConnectionLimiter limiter, ILogger<IrcGateway> logger)
        : base(limiter, logger)
    {
        _options = options;
        _upstream = upstream;
    }

    protected override string Name => "IRC";

    protected override IReadOnlyList<int> Ports => [_options.Irc.Port];

    protected override async Task HandleAsync(TcpClient client, IPAddress address, CancellationToken cancellationToken)
    {
        var tcp = client.GetStream();
        ClientWebSocket socket;
        try
        {
            socket = await _upstream.ConnectIrcAsync(address, cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            Logger.LogWarning(ex, "IRC: couldn't reach the main server for {Address}.", address);
            await tcp.WriteAsync(":gateway NOTICE * :The chat server can't be reached right now. Try again later.\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            return;
        }

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
    }
}
