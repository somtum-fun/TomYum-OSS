using TomYum.Bancho;
using TomYum.Server.Bancho.Packets;

namespace TomYum.Server.Bancho;

/// <summary>
/// Maps the osu!stable bancho HTTP surface. Stable rides over a single <c>POST /</c> on the
/// bancho subdomains (<c>c</c>/<c>c4</c>/<c>cho</c>): the first request (no <c>osu-token</c>
/// header) carries a plaintext login body; subsequent requests carry the token plus a binary
/// packet stream. See <c>memories/protocols/bancho-stable.md</c> §1–§3.
/// </summary>
/// <remarks>
/// Login mints a session and returns the success burst; authenticated polls decode the inbound
/// client packet stream via <see cref="BanchoPacketDispatcher"/>, then flush the player's
/// outbound queue plus anything a handler produced inline for the originator.
/// </remarks>
public static class BanchoEndpoints
{
    /// <summary>Response header carrying the minted session token on a successful login.</summary>
    private const string ChoTokenHeader = "cho-token";

    /// <summary>Request header identifying an existing bancho session on non-login requests.</summary>
    private const string OsuTokenHeader = "osu-token";

    private const string OctetStream = "application/octet-stream";

    /// <summary>
    /// Registers the bancho <c>POST /</c> handler and a browser-facing <c>GET /</c> page,
    /// constrained to the bancho subdomains (<c>c</c>/<c>c4</c>/<c>ce</c>/<c>cho</c>) plus
    /// localhost for development. The host patterns come from <see cref="BanchoHosts.Bancho"/>.
    /// </summary>
    public static void MapBancho(this IEndpointRouteBuilder app, params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Browsers hitting the bancho host get a human-readable status line; the osu! client
        // only ever POSTs, so this never interferes with the protocol.
        app.MapGet("/", (TomYumVersion version) =>
                Results.Text($"TomYum bancho {version.Display} is running.", "text/plain"))
            .RequireHost(hosts);

        app.MapPost("/", HandlePostAsync)
            .RequireHost(hosts);
    }

    private static async Task<IResult> HandlePostAsync(
        HttpContext context,
        BanchoLoginHandler loginHandler,
        BanchoPacketDispatcher dispatcher,
        PlayerRegistry registry,
        ChannelRegistry channels,
        CancellationToken cancellationToken)
    {
        // First contact: no osu-token header means the body is a plaintext login request.
        if (!context.Request.Headers.TryGetValue(OsuTokenHeader, out var tokenValues))
        {
            return await HandleLoginAsync(context, loginHandler, cancellationToken).ConfigureAwait(false);
        }

        var token = tokenValues.ToString();
        var player = registry.GetByToken(token);
        if (player is null)
        {
            // Unknown token (e.g. the server restarted): tell the client to reconnect.
            var writer = new BanchoPacketWriter(64);
            writer.WriteStringPacket(ServerPacketId.Notification, "TomYum has restarted — reconnecting.");
            writer.WritePacket(ServerPacketId.Restart, w => w.WriteInt32(0));
            return Results.Bytes(writer.ToArray(), OctetStream);
        }

        // Read the inbound packet stream, dispatch each packet (handlers may queue packets onto
        // this player or others), then flush this player's queue followed by anything the
        // handlers produced for the originator in-line.
        var body = await ReadBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
        var produced = await dispatcher.DispatchAsync(player, registry, channels, body).ConfigureAwait(false);

        var queued = player.DequeueAll();
        if (produced.Length == 0)
        {
            return Results.Bytes(queued, OctetStream);
        }

        if (queued.Length == 0)
        {
            return Results.Bytes(produced, OctetStream);
        }

        var combined = new byte[queued.Length + produced.Length];
        queued.CopyTo(combined, 0);
        produced.CopyTo(combined, queued.Length);
        return Results.Bytes(combined, OctetStream);
    }

    /// <summary>Reads the full request body into a byte array for packet decoding.</summary>
    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task<IResult> HandleLoginAsync(
        HttpContext context,
        BanchoLoginHandler loginHandler,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        var outcome = await loginHandler.HandleAsync(body, cancellationToken).ConfigureAwait(false);

        // The client reads cho-token from the response header on every login attempt; on
        // failure the value is irrelevant (it branches on the USER_ID packet) but must exist.
        context.Response.Headers[ChoTokenHeader] = outcome.Token ?? "no-token";
        return Results.Bytes(outcome.Body, OctetStream);
    }
}
