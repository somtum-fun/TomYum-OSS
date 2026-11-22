using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Resolves <see cref="Beatmap"/> metadata by MD5 hash, fronting the osu! v1 API
/// (<see cref="IOsuApiClient"/>) with an in-memory cache so repeat lookups of the same map are
/// free. Mirrors bancho.py's RAM-first resolution path; the DB tier and per-set cache-expiry
/// recheck cadence are deferred until <c>TomYum.Persistence</c> lands.
/// </summary>
/// <remarks>
/// A negative result (the API does not know the hash) is cached too — as a <c>null</c> sentinel —
/// so an unsubmitted map polled repeatedly by the client does not hammer the API. Clients send any
/// string as a checksum, so only real ones (32 hex digits) are looked up at all, and the cache is
/// bounded (<see cref="MaxCached"/> entries; maps kept a day after their last use, misses an hour).
/// <para>
/// With Redis, a ranked, approved or loved map found online is shared there for a day too (as the API gave it, before
/// any staff override): a restart, or another of the cluster's nodes, finds it there instead of asking the mirror
/// again (it allows a couple of requests a second, so a cold server's submissions on many maps queued behind it).
/// Only those statuses: a pending map may be ranked any time, and its next lookup must see that. Best effort: without
/// Redis, or with Redis down, lookups go online as before.
/// </para>
/// </remarks>
public sealed class BeatmapService : IDisposable
{
    /// <summary>Checksums cached at most.</summary>
    public const int MaxCached = 200_000;

    private static readonly TimeSpan FoundKept = TimeSpan.FromDays(1);
    private static readonly TimeSpan MissKept = TimeSpan.FromHours(1);

    private readonly IOsuApiClient _api;
    // Keys are lowercase checksums; values wrap the map so a miss (null) can be cached too.
    private readonly MemoryCache _byMd5 = new(new MemoryCacheOptions { SizeLimit = MaxCached });
    private readonly ConcurrentDictionary<int, byte> _fetchedSets = new();
    private readonly ConcurrentDictionary<int, RankedStatus> _statusOverrides = new();

    private readonly IServiceScopeFactory? _scopes;
    private readonly IDatabase? _shared;

    private const string SharedMapPrefix = "tomyum:beatmaps:md5:";
    private static readonly TimeSpan SharedWait = TimeSpan.FromSeconds(1);

    /// <summary>Creates the service over the osu! API client.</summary>
    /// <param name="api">The osu! API.</param>
    /// <param name="scopes">When given, maps the API doesn't know are looked up in this server's own
    /// beatmaps too: custom (uploaded) sets exist only there.</param>
    /// <param name="redis">When given, maps found online are shared through it (a restart, the cluster's other nodes).</param>
    public BeatmapService(IOsuApiClient api, IServiceScopeFactory? scopes = null, IConnectionMultiplexer? redis = null)
    {
        _api = api;
        _scopes = scopes;
        _shared = redis?.GetDatabase();
    }

    // The map as osu! (or the mirror) gave it, from Redis; null when it isn't there or Redis can't answer.
    private async Task<Beatmap?> SharedAsync(string md5)
    {
        if (_shared is null)
            return null;
        try
        {
            var value = await _shared.StringGetAsync(SharedMapPrefix + md5).WaitAsync(SharedWait).ConfigureAwait(false);
            return !value.IsNullOrEmpty && JsonSerializer.Deserialize<Beatmap>(value.ToString()) is { Id: > 0, SetId: > 0 } map
                && string.Equals(map.Md5, md5, StringComparison.OrdinalIgnoreCase) ? map : null;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            return null;
        }
    }

    // A map osu! (or the mirror) just gave, before any staff override, into Redis for the others: one whose status
    // won't change under us (ranked, approved, loved) only.
    private void Share(Beatmap beatmap)
    {
        if (_shared is null || beatmap.IsCustom || !IsMd5(beatmap.Md5)
            || beatmap.Status is not (RankedStatus.Ranked or RankedStatus.Approved or RankedStatus.Loved))
            return;
        try
        {
            _shared.StringSet(SharedMapPrefix + beatmap.Md5.ToLowerInvariant(), JsonSerializer.Serialize(beatmap), FoundKept, flags: CommandFlags.FireAndForget);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
        }
    }

    public void Dispose() => _byMd5.Dispose();

    /// <summary>Number of MD5 hashes currently cached (including negative not-found entries).</summary>
    public int CachedCount => _byMd5.Count;

    /// <summary>Whether <paramref name="md5"/> looks like a checksum (32 hex digits).</summary>
    public static bool IsMd5(string? md5) => md5 is { Length: 32 } && md5.All(char.IsAsciiHexDigit);

    private sealed record Cached(Beatmap? Beatmap);

    private void Cache(string md5, Beatmap? beatmap)
    {
        if (!IsMd5(md5))
            return;
        using var entry = _byMd5.CreateEntry(md5.ToLowerInvariant());
        entry.Value = new Cached(beatmap);
        entry.Size = 1;
        if (beatmap is null)
            entry.AbsoluteExpirationRelativeToNow = MissKept;
        else
            entry.SlidingExpiration = FoundKept;
    }

    /// <summary>The cached checksums and their maps (misses have none).</summary>
    private IEnumerable<(string Md5, Beatmap? Beatmap)> CachedEntries()
    {
        foreach (var key in _byMd5.Keys)
        {
            if (key is string md5 && _byMd5.TryGetValue(md5, out Cached? cached) && cached is not null)
                yield return (md5, cached.Beatmap);
        }
    }

    /// <summary>
    /// Makes beatmap <paramref name="beatmapId"/> report <paramref name="status"/> (a staff override,
    /// stored frozen in the database) instead of what the osu! API says — leaderboards and pp follow
    /// it. Applies to cached lookups immediately.
    /// </summary>
    public void SetStatusOverride(int beatmapId, RankedStatus status)
    {
        _statusOverrides[beatmapId] = status;
        foreach (var (_, beatmap) in CachedEntries())
        {
            if (beatmap?.Id == beatmapId)
                ApplyOverride(beatmap);
        }
    }

    /// <summary>The staff override for <paramref name="beatmapId"/>, if any.</summary>
    public RankedStatus? GetStatusOverride(int beatmapId) =>
        _statusOverrides.TryGetValue(beatmapId, out var status) ? status : null;

    /// <summary>
    /// Drops the staff override for <paramref name="beatmapId"/>. Cached lookups of it are forgotten,
    /// so the next one (or a <see cref="FetchSetAsync"/>) takes the osu! API's status again.
    /// </summary>
    public void ClearStatusOverride(int beatmapId)
    {
        _statusOverrides.TryRemove(beatmapId, out _);
        foreach (var (md5, beatmap) in CachedEntries().ToList())
        {
            if (beatmap?.Id == beatmapId)
                _byMd5.Remove(md5);
        }
    }

    private Beatmap? ApplyOverride(Beatmap? beatmap)
    {
        if (beatmap is not null && _statusOverrides.TryGetValue(beatmap.Id, out var status))
        {
            beatmap.Status = status;
            beatmap.StatusFrozen = true;
            beatmap.OsuApiStatus = null; // the API's value no longer describes this map
        }
        return beatmap;
    }

    /// <summary>
    /// Resolves a beatmap by its <c>.osu</c> MD5 hash: the osu! API's map, or else a custom map of
    /// this server's. Returns <c>null</c> when neither knows it (treat as
    /// <see cref="Domain.Enums.RankedStatus.NotSubmitted"/>). Results — including the not-found
    /// result — are cached, but not a failed osu! lookup (osu! didn't answer), which is asked again.
    /// Anything but a checksum (<see cref="IsMd5"/>) is no map, without asking.
    /// </summary>
    public async Task<Beatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default)
    {
        if (!IsMd5(md5))
        {
            return null;
        }

        md5 = md5.ToLowerInvariant();
        if (_byMd5.TryGetValue(md5, out Cached? cached) && cached is not null)
        {
            return cached.Beatmap;
        }

        if (await SharedAsync(md5).ConfigureAwait(false) is { } shared)
        {
            var known = ApplyOverride(shared);
            Cache(md5, known);
            return known;
        }

        var failed = new StrongBox<bool>();
        Beatmap? fromApi;
        using (OsuApiFailures.Watch(failed))
            fromApi = await _api.GetByMd5Async(md5, cancellationToken).ConfigureAwait(false);
        if (fromApi is not null)
            Share(fromApi);
        var stored = fromApi is null ? await GetStoredAsync(md5, cancellationToken).ConfigureAwait(false) : null;
        var resolved = ApplyOverride(fromApi ?? (stored is { IsCustom: true } ? stored : null));
        // osu! (or the mirror) didn't answer, which isn't osu! not knowing the map: a map this server has stored (it was
        // played here before) is taken as stored, so a score on it isn't refused as one on an unknown map. Not cached:
        // the next lookup asks osu! again.
        if (resolved is null && failed.Value && stored is not null)
            return ApplyOverride(stored);
        // osu! not answering isn't osu! not knowing the map: ask again next time instead of
        // remembering it as unsubmitted.
        if (resolved is not null || !failed.Value)
            Cache(md5, resolved);
        return resolved;
    }

    private readonly ConcurrentDictionary<string, Task> _warming = new(StringComparer.Ordinal);

    // Checksums whose background lookup just failed (osu! not answering): not asked again for a minute.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _warmFailed = new(StringComparer.Ordinal);

    /// <summary>
    /// <see cref="GetByMd5Async(string, CancellationToken)"/> for a read that only needs the map's id, set and status
    /// (a leaderboard): a map not cached yet is taken from TomYum's own table at once, when it's there, and the osu!
    /// lookup runs in the background to cache the full map for later. Saves the osu! API's (or the mirror's) round
    /// trip, hundreds of milliseconds, on the first leaderboard of every map. Anything that stores the map (a score
    /// submission, an import) keeps using <see cref="GetByMd5Async(string, CancellationToken)"/>: the stored row has no
    /// API status or metadata for the website.
    /// </summary>
    public async Task<Beatmap?> GetByMd5ForReadAsync(string md5, CancellationToken cancellationToken = default)
    {
        if (!IsMd5(md5))
            return null;
        md5 = md5.ToLowerInvariant();
        if (_byMd5.TryGetValue(md5, out Cached? cached) && cached is not null)
            return cached.Beatmap;

        // Only a status that won't change under us (ranked, approved, loved, or held by staff): a pending map may have
        // been ranked since it was stored, so that one waits for the full lookup.
        if (await GetStoredAsync(md5, cancellationToken).ConfigureAwait(false) is { } stored
            && (stored.StatusFrozen || stored.Status is RankedStatus.Ranked or RankedStatus.Approved or RankedStatus.Loved))
        {
            WarmInBackground(md5);
            return ApplyOverride(stored);
        }
        return await GetByMd5Async(md5, cancellationToken).ConfigureAwait(false);
    }

    // The full lookup for md5, once at a time, off the request.
    private void WarmInBackground(string md5)
    {
        if (_warming.ContainsKey(md5)
            || (_warmFailed.TryGetValue(md5, out var failedAt) && DateTimeOffset.UtcNow - failedAt < TimeSpan.FromMinutes(1)))
            return;
        var task = new Task<Task>(async () =>
        {
            try
            {
                await GetByMd5Async(md5, CancellationToken.None).ConfigureAwait(false);
                // Not cached: osu! didn't answer (a miss would have been cached).
                if (_byMd5.TryGetValue(md5, out Cached? _))
                    _warmFailed.TryRemove(md5, out _);
                else
                    _warmFailed[md5] = DateTimeOffset.UtcNow;
            }
#pragma warning disable CA1031 // a background warm-up failing only means the next request asks again
            catch (Exception)
#pragma warning restore CA1031
            {
            }
            finally
            {
                _warming.TryRemove(md5, out _);
            }
        });
        if (_warming.TryAdd(md5, task))
            task.Start(TaskScheduler.Default);
    }

    private async Task<Beatmap?> GetStoredAsync(string md5, CancellationToken cancellationToken)
    {
        if (_scopes is null)
            return null;
        await using var scope = _scopes.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IBeatmapRepository>() is not { } repository)
            return null;
        return await repository.GetByMd5Async(md5, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes custom maps just stored (an upload) instead of what was cached for their checksums, so a
    /// client that asked before the upload sees them now.
    /// </summary>
    public void Remember(IEnumerable<Beatmap> beatmaps)
    {
        ArgumentNullException.ThrowIfNull(beatmaps);
        foreach (var beatmap in beatmaps)
        {
            foreach (var (md5, cached) in CachedEntries().ToList())
            {
                if (cached?.Id == beatmap.Id)
                    _byMd5.Remove(md5);
            }
            Cache(beatmap.Md5, ApplyOverride(beatmap));
        }
    }

    /// <summary>
    /// Fetches every difficulty of set <paramref name="setId"/> the first time it is asked for in
    /// this process, and returns empty on later calls — so the caller imports each set once per
    /// run instead of on every play. A failed or empty fetch is retried on the next call. Official
    /// sets only: server-local custom sets aren't on the osu! API.
    /// </summary>
    public async Task<IReadOnlyList<Beatmap>> FetchSetOnceAsync(int setId, CancellationToken cancellationToken = default)
    {
        if (!IsOfficialSet(setId) || !_fetchedSets.TryAdd(setId, 0))
        {
            return [];
        }

        var set = await FetchAndCacheAsync(setId, cancellationToken).ConfigureAwait(false);
        if (set.Count == 0)
        {
            _fetchedSets.TryRemove(setId, out _);
        }
        return set;
    }

    /// <summary>
    /// Fetches every difficulty of set <paramref name="setId"/> from the API now, whether or not it
    /// was fetched before (staff imports want current data). Empty for unknown or custom sets.
    /// </summary>
    public async Task<IReadOnlyList<Beatmap>> FetchSetAsync(int setId, CancellationToken cancellationToken = default)
    {
        if (!IsOfficialSet(setId))
        {
            return [];
        }

        var set = await FetchAndCacheAsync(setId, cancellationToken).ConfigureAwait(false);
        if (set.Count > 0)
        {
            _fetchedSets.TryAdd(setId, 0);
        }
        return set;
    }

    private static bool IsOfficialSet(int setId) => setId is > 0 and < Beatmap.CustomIdFloor;

    private async Task<IReadOnlyList<Beatmap>> FetchAndCacheAsync(int setId, CancellationToken cancellationToken)
    {
        var set = await _api.GetSetAsync(setId, cancellationToken).ConfigureAwait(false);

        // The set's difficulties are now known: later lookups of them skip the API.
        foreach (var beatmap in set)
        {
            Share(beatmap);
            Cache(beatmap.Md5, ApplyOverride(beatmap));
        }
        return set;
    }
}
