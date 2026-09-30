using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.LastFm;

/// <summary>
/// Scrobbles the plays of players who connected Last.fm (account settings on the website): every
/// passed play, stable's and lazer's alike (both are on the website), as the map's song, with the
/// song's own artist and title (in its original script when the map has it) and its length at the
/// play's speed. Songs under 30 seconds aren't scrobbled (Last.fm's rule). While they play, Last.fm
/// shows the song as "now playing". A player who takes the server's access away on Last.fm is
/// disconnected here too.
/// </summary>
public sealed class LastFmScrobbler : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>Last.fm doesn't take songs shorter than this.</summary>
    public const int MinimumSeconds = 30;

    private readonly IServiceScopeFactory _scopes;
    private readonly LastFmClient _lastFm;
    private readonly OnlinePlayers? _online;
    private readonly ILogger<LastFmScrobbler> _logger;

    // Player → the map last sent as "now playing".
    private readonly Dictionary<int, int> _nowPlaying = [];

    public LastFmScrobbler(IServiceScopeFactory scopes, LastFmClient lastFm, ILogger<LastFmScrobbler> logger, OnlinePlayers? online = null)
    {
        _scopes = scopes;
        _lastFm = lastFm;
        _online = online;
        _logger = logger;
    }

    /// <summary>The song as Last.fm gets it: original-script names when there are some, length at the play's speed.</summary>
    public static LastFmTrack Track(string artist, string artistUnicode, string title, string titleUnicode, long lengthSeconds, double speed = 1) =>
        new(string.IsNullOrWhiteSpace(artistUnicode) ? artist : artistUnicode,
            string.IsNullOrWhiteSpace(titleUnicode) ? title : titleUnicode,
            (int)Math.Round(lengthSeconds / (speed > 0 ? speed : 1)));

    /// <summary>How fast a score's mods play the song (DT/NC 1.5, HT/DC 0.75, or their speed setting).</summary>
    public static double Speed(string scoreData)
    {
        try
        {
            using var data = JsonDocument.Parse(string.IsNullOrWhiteSpace(scoreData) ? "{}" : scoreData);
            if (!data.RootElement.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array)
                return 1;
            foreach (var mod in mods.EnumerateArray())
            {
                var acronym = mod.TryGetProperty("acronym", out var a) ? a.GetString() : null;
                double? normal = acronym switch
                {
                    "DT" or "NC" => 1.5,
                    "HT" or "DC" => 0.75,
                    _ => null,
                };
                if (normal is null)
                    continue;
                return mod.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object
                       && settings.TryGetProperty("speed_change", out var change) && change.TryGetDouble(out var value) && value > 0
                    ? value
                    : normal.Value;
            }
        }
        catch (JsonException)
        {
            // A score without readable mods plays at normal speed.
        }
        return 1;
    }

    /// <summary>Scrobbles the new passed plays of connected players; returns how many.</summary>
    public async Task<int> ScrobbleAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
        var website = scope.ServiceProvider.GetRequiredService<OsuWebScrobbles>();

        if (await website.MarkAsync(cancellationToken).ConfigureAwait(false) is not { } mark)
        {
            await website.SetMarkAsync(await website.NewestScoreIdAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var plays = await website.AfterAsync(mark, 100, cancellationToken).ConfigureAwait(false);
        if (plays.Count == 0)
            return 0;
        var links = await db.LastFmLinks.ToDictionaryAsync(l => l.UserId, cancellationToken).ConfigureAwait(false);

        var scrobbled = 0;
        foreach (var play in plays)
        {
            if (play.Passed && links.TryGetValue((int)play.UserId, out var link))
            {
                var track = Track(play.Artist, play.ArtistUnicode, play.Title, play.TitleUnicode, play.Length, Speed(play.Data));
                if (track.DurationSeconds >= MinimumSeconds)
                {
                    var endedAt = new DateTimeOffset(DateTime.SpecifyKind(play.EndedAt, DateTimeKind.Utc));
                    var startedAt = play.StartedAt is { } started
                        ? new DateTimeOffset(DateTime.SpecifyKind(started, DateTimeKind.Utc))
                        : endedAt.AddSeconds(-track.DurationSeconds);
                    try
                    {
                        await _lastFm.ScrobbleAsync(link.SessionKey, track, startedAt, cancellationToken).ConfigureAwait(false);
                        scrobbled++;
                    }
                    catch (LastFmException ex) when (ex.SessionInvalid)
                    {
                        await DisconnectAsync(db, link.UserId, links, cancellationToken).ConfigureAwait(false);
                    }
                    catch (LastFmException ex) when (ex.Temporary)
                    {
                        // Last.fm is having trouble: this play (and those after it) wait for the next pass.
                        _logger.LogInformation("Last.fm: {Message}; scrobbling later.", ex.Message);
                        return scrobbled;
                    }
                    catch (LastFmException ex)
                    {
                        _logger.LogWarning("Last.fm refused score {ScoreId} ({Code}: {Message}); skipped.", play.ScoreId, ex.Code, ex.Message);
                    }
                }
            }
            await website.SetMarkAsync(play.ScoreId, cancellationToken).ConfigureAwait(false);
        }
        return scrobbled;
    }

    /// <summary>Tells Last.fm what connected players started playing since the last pass.</summary>
    public async Task NowPlayingAsync(CancellationToken cancellationToken = default)
    {
        if (_online is null)
            return;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TomYumDbContext>();
        var links = await db.LastFmLinks.ToDictionaryAsync(l => l.UserId, cancellationToken).ConfigureAwait(false);
        if (links.Count == 0)
        {
            _nowPlaying.Clear();
            return;
        }

        var website = scope.ServiceProvider.GetRequiredService<OsuWebScrobbles>();
        var playing = new HashSet<int>();
        foreach (var entry in await _online.GetAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!entry.Playing || entry.BeatmapId is not { } beatmapId || !links.TryGetValue(entry.UserId, out var link))
                continue;
            playing.Add(entry.UserId);
            if (_nowPlaying.TryGetValue(entry.UserId, out var sent) && sent == beatmapId)
                continue;
            _nowPlaying[entry.UserId] = beatmapId;

            if (await website.SongAsync(beatmapId, cancellationToken).ConfigureAwait(false) is not { } song)
                continue;
            var track = Track(song.Artist, song.ArtistUnicode, song.Title, song.TitleUnicode, song.Length);
            try
            {
                await _lastFm.NowPlayingAsync(link.SessionKey, track, cancellationToken).ConfigureAwait(false);
            }
            catch (LastFmException ex) when (ex.SessionInvalid)
            {
                await DisconnectAsync(db, link.UserId, links, cancellationToken).ConfigureAwait(false);
            }
            catch (LastFmException ex)
            {
                _logger.LogInformation("Last.fm didn't take \"now playing\" ({Code}: {Message}).", ex.Code, ex.Message);
            }
        }

        // Stopped playing: the next map is news again.
        foreach (var userId in _nowPlaying.Keys.Where(id => !playing.Contains(id)).ToList())
            _nowPlaying.Remove(userId);
    }

    private async Task DisconnectAsync(TomYumDbContext db, int userId, Dictionary<int, Domain.Entities.LastFmLink> links, CancellationToken cancellationToken)
    {
        await db.LastFmLinks.Where(l => l.UserId == userId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        links.Remove(userId);
        _logger.LogInformation("Last.fm: user {UserId} took the server's access away; disconnected.", userId.ToString(CultureInfo.InvariantCulture));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await NowPlayingAsync(stoppingToken).ConfigureAwait(false);
                await ScrobbleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Last.fm or the database having a moment: the plays wait for the next pass
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "Last.fm: a pass failed; trying again shortly.");
            }
            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
