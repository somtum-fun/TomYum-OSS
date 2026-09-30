using Microsoft.EntityFrameworkCore;
using TomYum.Domain.Entities;
using TomYum.Persistence;

namespace TomYum.Server.LastFm;

/// <summary>What the website sends to connect a Last.fm account: the token Last.fm gave it.</summary>
public sealed record LastFmLinkRequest(string? Token);

/// <summary>
/// Admin API for the website's account settings: whether Last.fm is set up and the player's
/// connected account, connecting (Last.fm's token for a session key) and disconnecting. The key and
/// secret stay here; the website only sees the address to send players to.
/// </summary>
public static class LastFmEndpoints
{
    public static void MapLastFm(this RouteGroupBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);

        admin.MapGet("/lastfm/{userId:int}", async (int userId, string? callback, IServiceProvider services, TomYumDbContext db, CancellationToken cancellationToken) =>
        {
            var client = services.GetService<LastFmClient>();
            var link = await db.LastFmLinks.AsNoTracking().FirstOrDefaultAsync(l => l.UserId == userId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                configured = client is not null,
                username = link?.Username,
                linkedAt = link?.LinkedAt,
                authUrl = client is not null && !string.IsNullOrWhiteSpace(callback) ? client.AuthUrl(callback) : null,
            });
        });

        admin.MapPost("/lastfm/{userId:int}/link", async (int userId, LastFmLinkRequest request, IServiceProvider services, TomYumDbContext db,
            TimeProvider time, CancellationToken cancellationToken) =>
        {
            if (services.GetService<LastFmClient>() is not { } client)
                return Results.Problem("Last.fm isn't set up on this server.", statusCode: StatusCodes.Status503ServiceUnavailable);
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.BadRequest(new { error = "No token from Last.fm." });

            (string Username, string SessionKey) session;
            try
            {
                session = await client.GetSessionAsync(request.Token, cancellationToken).ConfigureAwait(false);
            }
            catch (LastFmException ex)
            {
                return Results.BadRequest(new { error = $"Last.fm said no: {ex.Message}" });
            }

            var link = await db.LastFmLinks.FirstOrDefaultAsync(l => l.UserId == userId, cancellationToken).ConfigureAwait(false);
            if (link is null)
            {
                link = new LastFmLink { UserId = userId };
                db.LastFmLinks.Add(link);
            }
            link.Username = session.Username;
            link.SessionKey = session.SessionKey;
            link.LinkedAt = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { username = link.Username });
        });

        admin.MapPost("/lastfm/{userId:int}/unlink", async (int userId, TomYumDbContext db, CancellationToken cancellationToken) =>
        {
            await db.LastFmLinks.Where(l => l.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok();
        });
    }
}
