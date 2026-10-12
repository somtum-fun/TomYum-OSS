using System.Collections.Concurrent;
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
    /// <summary>How long a "listening to" answer is reused (profiles are viewed often; Last.fm is asked at most this often).</summary>
    private static readonly TimeSpan ListeningFor = TimeSpan.FromSeconds(10);

    /// <summary>How long a profile waits for Last.fm before showing nothing.</summary>
    private static readonly TimeSpan ListeningWait = TimeSpan.FromSeconds(3);

    private static readonly ConcurrentDictionary<int, (DateTimeOffset At, LastFmListening? Listening)> Listening = new();

    // The settings page asks for the history every 10 seconds too: Last.fm at most that often per player.
    private static readonly ConcurrentDictionary<int, (DateTimeOffset At, IReadOnlyList<LastFmRecentTrack> Tracks)> History = new();

    /// <summary>A track of a player's Last.fm history, and where it was played when it came from osu! here.</summary>
    public sealed record RecentTrack(string Artist, string Title, string? Url, DateTimeOffset? At, bool NowPlaying, string? Client, int? BeatmapId, string? Image = null);

    /// <summary>How far apart Last.fm's time and ours can be for a track to be the same play.</summary>
    private static readonly TimeSpan SamePlay = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The player's Last.fm history (Spotify and the rest too), newest first; the plays scrobbled from
    /// osu! here say where they were played. When Last.fm can't be asked, only the plays from here.
    /// </summary>
    public static async Task<IReadOnlyList<RecentTrack>> RecentAsync(LastFmClient client, TomYumDbContext db, LastFmLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(link);

        var ours = await db.LastFmScrobbles.AsNoTracking().Where(s => s.UserId == link.UserId).OrderByDescending(s => s.Id)
            .Take(LastFmScrobbler.KeptPerPlayer).ToListAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<LastFmRecentTrack> history;
        if (History.TryGetValue(link.UserId, out var cached) && DateTimeOffset.UtcNow - cached.At <= ListeningFor)
            history = cached.Tracks;
        else
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            wait.CancelAfter(ListeningWait);
            try
            {
                history = await client.RecentAsync(link.Username, 10, wait.Token).ConfigureAwait(false);
                History[link.UserId] = (DateTimeOffset.UtcNow, history);
            }
            catch (Exception ex) when (ex is LastFmException or HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return [.. ours.Take(10).Select(s => new RecentTrack(s.Artist, s.Title, null,
                    new DateTimeOffset(DateTime.SpecifyKind(s.ScrobbledAt, DateTimeKind.Utc)), false, s.Client, s.BeatmapId))];
            }
        }

        return [.. history.Select(track =>
        {
            var play = track.At is { } at
                ? ours.FirstOrDefault(s => string.Equals(s.Artist, track.Artist, StringComparison.OrdinalIgnoreCase)
                                           && string.Equals(s.Title, track.Title, StringComparison.OrdinalIgnoreCase)
                                           && (new DateTimeOffset(DateTime.SpecifyKind(s.ScrobbledAt, DateTimeKind.Utc)) - at).Duration() <= SamePlay)
                : null;
            return new RecentTrack(track.Artist, track.Title, track.Url, track.At, track.NowPlaying, play?.Client, play?.BeatmapId, track.Image);
        })];
    }

    public static void MapLastFm(this RouteGroupBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);

        admin.MapGet("/lastfm/{userId:int}", async (int userId, string? callback, IServiceProvider services, TomYumDbContext db, CancellationToken cancellationToken) =>
        {
            var client = services.GetService<LastFmClient>();
            var link = await db.LastFmLinks.AsNoTracking().FirstOrDefaultAsync(l => l.UserId == userId, cancellationToken).ConfigureAwait(false);
            var recent = link is null || client is null
                ? []
                : await RecentAsync(client, db, link, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                configured = client is not null,
                username = link?.Username,
                linkedAt = link?.LinkedAt,
                recent,
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

        // What a connected player is listening to (Last.fm's "now playing") and their Last.fm name, for their
        // profile; 204 when not connected.
        admin.MapGet("/lastfm/{userId:int}/listening", async (int userId, IServiceProvider services, TomYumDbContext db, TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            if (services.GetService<LastFmClient>() is not { } client)
                return Results.NoContent();

            var now = time.GetUtcNow();
            var username = await db.LastFmLinks.AsNoTracking().Where(l => l.UserId == userId).Select(l => l.Username)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (username is null)
                return Results.NoContent();
            if (!Listening.TryGetValue(userId, out var cached) || now - cached.At > ListeningFor)
            {
                LastFmListening? listening = null;
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    wait.CancelAfter(ListeningWait);
                    try
                    {
                        listening = await client.ListeningAsync(username, wait.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is LastFmException or HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                    {
                        // Last.fm slow or unhappy: the profile shows nothing for a while.
                    }
                }
                cached = (now, listening);
                Listening[userId] = cached;
            }
            return Results.Ok(new { listening = cached.Listening, username });
        });

        admin.MapPost("/lastfm/{userId:int}/unlink", async (int userId, TomYumDbContext db, CancellationToken cancellationToken) =>
        {
            await db.LastFmLinks.Where(l => l.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.LastFmScrobbles.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            Listening.TryRemove(userId, out _);
            History.TryRemove(userId, out _);
            return Results.Ok();
        });
    }
}
