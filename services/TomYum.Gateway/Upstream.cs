using System.Net;
using System.Net.WebSockets;

namespace TomYum.Gateway;

/// <summary>
/// Connections to the main server: WebSockets to its bancho host, with the gateway's key and the
/// player's address (the main server trusts that address only with the key, docs/gateway.md).
/// </summary>
public sealed class Upstream : IDisposable
{
    public const string KeyHeader = "X-TomYum-Gateway-Key";
    public const string ClientIpHeader = "X-TomYum-Gateway-Ip";

    private readonly GatewayOptions _options;
    private readonly SocketsHttpHandler _handler;

    public Upstream(GatewayOptions options)
    {
        _options = options;
        _handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
    }

    /// <summary>Opens the WebSocket for one connection from <paramref name="player"/> to <paramref name="path"/>.</summary>
    public async Task<ClientWebSocket> ConnectAsync(string path, IPAddress player, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);
        var builder = new UriBuilder(new Uri(_options.UpstreamUri, path));
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Host", _options.UpstreamHost);
        socket.Options.SetRequestHeader(KeyHeader, _options.Key);
        socket.Options.SetRequestHeader(ClientIpHeader, player.ToString());
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        try
        {
            using var invoker = new HttpMessageInvoker(_handler, disposeHandler: false);
            await socket.ConnectAsync(builder.Uri, invoker, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public void Dispose() => _handler.Dispose();
}
