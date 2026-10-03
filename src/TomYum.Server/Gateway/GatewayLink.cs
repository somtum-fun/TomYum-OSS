using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using TomYum.Server.Irc;

namespace TomYum.Server.Gateway;

/// <summary>Settings for the link with TomYum.Gateway (section <c>Gateway</c>, docs/gateway.md).</summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>
    /// The secret the gateway sends with every request (the same <c>Gateway:Key</c> on both). Empty
    /// turns the link off: the player address it passes on isn't trusted and its IRC relay is refused.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    public bool Enabled => Key.Length > 0;
}

/// <summary>
/// The main server's side of TomYum.Gateway, the edge that takes raw TCP (pre-2014 bancho on
/// <c>server.&lt;domain&gt;</c>, IRC) so the main server only ever needs HTTPS. The gateway turns TCP
/// bancho into ordinary HTTP bancho requests and carries IRC connections over a WebSocket; this
/// trusts the player address it passes on (when its key checks out) and runs those IRC connections.
/// </summary>
public static class GatewayLink
{
    /// <summary>The gateway's secret (<see cref="GatewayOptions.Key"/>).</summary>
    public const string KeyHeader = "X-TomYum-Gateway-Key";

    /// <summary>The player's address as the gateway saw it.</summary>
    public const string ClientIpHeader = "X-TomYum-Gateway-Ip";

    /// <summary>
    /// For requests from the gateway: the player's address replaces the gateway's in <c>X-Real-IP</c>
    /// (what login IPs, countries and limits use), and Cloudflare's <c>CF-IPCountry</c> (the
    /// gateway's country) is dropped so the country comes from the player's address. Anything else
    /// claiming a player address has it ignored.
    /// </summary>
    public static IApplicationBuilder UseGatewayLink(this IApplicationBuilder app, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return app.Use(async (context, next) =>
        {
            var headers = context.Request.Headers;
            if (headers.ContainsKey(ClientIpHeader))
            {
                if (IsGateway(context, options) && IPAddress.TryParse(headers[ClientIpHeader].ToString().Trim(), out var player))
                {
                    headers["X-Real-IP"] = player.ToString();
                    headers.Remove("CF-IPCountry");
                }
                headers.Remove(ClientIpHeader);
            }
            await next(context).ConfigureAwait(false);
        });
    }

    /// <summary>Whether the request carries the gateway's key.</summary>
    public static bool IsGateway(HttpContext context, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
            return false;
        var sent = context.Request.Headers[KeyHeader].ToString();
        return sent.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(options.Key));
    }

    /// <summary>
    /// <c>/_gateway/irc</c> on the bancho hosts: one IRC connection the gateway accepted, as a
    /// WebSocket carrying the IRC bytes both ways. Only for the gateway (404 otherwise).
    /// </summary>
    public static void MapGatewayIrc(this IEndpointRouteBuilder app, string[] hosts)
    {
        app.Map("/_gateway/irc", async (HttpContext context, GatewayOptions options, IServiceProvider services, IHostApplicationLifetime lifetime) =>
        {
            if (!IsGateway(context, options) || !context.WebSockets.IsWebSocketRequest)
                return Results.NotFound();

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await using var stream = WebSocketStream.Create(socket, WebSocketMessageType.Binary, ownsWebSocket: false);
            using var session = ActivatorUtilities.CreateInstance<IrcSession>(services, (Stream)stream);
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
            await session.RunAsync(stopping.Token).ConfigureAwait(false);
            if (socket.State == WebSocketState.Open)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
                {
                }
            }
            return Results.Empty;
        }).RequireHost(hosts);
    }
}
