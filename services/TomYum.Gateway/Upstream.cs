using System.Net;
using System.Net.Http.Headers;

namespace TomYum.Gateway;

/// <summary>
/// Requests to the main server: its bancho host, the gateway's key, and the player's address (the
/// main server trusts that address only with the key, docs/gateway.md).
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
            AutomaticDecompression = DecompressionMethods.None,
        };
        Http = new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Shared by every session (one connection pool to the main server).</summary>
    public HttpClient Http { get; }

    /// <summary>A bancho request (login or poll) from the player at <paramref name="player"/>.</summary>
    public HttpRequestMessage BanchoRequest(IPAddress player, byte[] body, string? token)
    {
        ArgumentNullException.ThrowIfNull(player);
        var request = new HttpRequestMessage(HttpMethod.Post, _options.UpstreamUri) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Host = _options.UpstreamHost;
        request.Headers.TryAddWithoutValidation("User-Agent", "osu!");
        if (token is not null)
            request.Headers.TryAddWithoutValidation("osu-token", token);
        AddGatewayHeaders(request.Headers, player);
        return request;
    }

    /// <summary>Opens the IRC relay WebSocket for the player at <paramref name="player"/>.</summary>
    public async Task<System.Net.WebSockets.ClientWebSocket> ConnectIrcAsync(IPAddress player, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);
        var builder = new UriBuilder(new Uri(_options.UpstreamUri, "_gateway/irc"));
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        var socket = new System.Net.WebSockets.ClientWebSocket();
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

    private void AddGatewayHeaders(HttpRequestHeaders headers, IPAddress player)
    {
        headers.TryAddWithoutValidation(KeyHeader, _options.Key);
        headers.TryAddWithoutValidation(ClientIpHeader, player.ToString());
    }

    public void Dispose()
    {
        Http.Dispose();
        _handler.Dispose();
    }
}
