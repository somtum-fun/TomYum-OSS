using TomYum.Bancho;

namespace TomYum.Server.Bancho;

/// <summary>
/// Maps the osu!stable bancho HTTP surface. Stable rides over a single <c>POST /</c> on the
/// bancho subdomains (<c>c</c>/<c>c4</c>/<c>cho</c>): the first request (no <c>osu-token</c>
/// header) carries a plaintext login body; subsequent requests carry the token plus a binary
/// packet stream. See <c>memories/protocols/bancho-stable.md</c> §1–§3.
/// </summary>
/// <remarks>
/// This slice implements login + the per-player outbound queue flush. Packet dispatch for
/// post-login client packets (chat, presence, spectate, multiplayer) is a later slice; for
/// now an authenticated poll simply drains whatever the server queued for that player.
/// </remarks>
public static class BanchoEndpoints
{
    /// <summary>Response header carrying the minted session token on a successful login.</summary>
    private const string ChoTokenHeader = "cho-token";

    /// <summary>Request header identifying an existing bancho session on non-login requests.</summary>
    private const string OsuTokenHeader = "osu-token";

    private const string OctetStream = "application/octet-stream";

    /// <summary>Registers the bancho <c>POST /</c> handler and a browser-facing <c>GET /</c> page.</summary>
    public static void MapBancho(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Browsers hitting the bancho host get a human-readable status line; the osu! client
        // only ever POSTs, so this never interferes with the protocol.
        app.MapGet("/", () => Results.Text("TomYum bancho is running.", "text/plain"));

        app.MapPost("/", HandlePostAsync);
    }

    private static async Task<IResult> HandlePostAsync(
        HttpContext context,
        BanchoLoginHandler loginHandler,
        PlayerRegistry registry,
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

        // TODO (next slice): decode and dispatch the inbound client packet stream here.
        // For now, drain and flush whatever the server has queued for this player.
        player.LastActivityUtc = DateTimeOffset.UtcNow;
        return Results.Bytes(player.DequeueAll(), OctetStream);
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
