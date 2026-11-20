// Stable spectate plugin (experimental): TumThai (osu!lazer) players see and watch osu!(stable) players.
//
// TumThai's setting "Show osu!(stable) players" (off by default) asks the spectator server for them; as on stable,
// anyone online can be seen and spectated. This plugin and tomyum-spectator (TomYumStableSpectate.cs) share
// three Redis keys:
//   tomyum:stable-spectate:players  (this plugin, every couple of seconds) the stable players online, with what
//                                   they're playing; the spectator server shows them to those TumThai clients
//                                   (an "osu!(stable)" activity, so lazer offers Spectate).
//   tomyum:stable-spectate:watched  (spectator server) the stable players someone on lazer is watching: the
//                                   server's bot spectates them so their client streams its frames.
//   tomyum:stable-spectate:frames   (this plugin, pub/sub) their frame bundles, read into frames and scores,
//                                   which the spectator server passes to lazer as lazer frames.
//   tomyum:stable-spectate:watchers (spectator server) who on lazer watches each of them: the player sees those
//                                   accounts as spectators, and the bot watches quietly (it shows only when
//                                   nobody can be shown, so the client still streams).
// The bot is detached only from players this plugin attached it to, and never during a play (osu!dan also
// spectates with it: dan detaches when an attempt ends). Human docs: README.md next to this file.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using StackExchange.Redis;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.StableSpectate;

/// <summary>osu!lazer viewers for osu!(stable) players (experimental).</summary>
#pragma warning disable CA1001 // _cts is disposed in ShutdownAsync, the end of a plugin's life
public sealed class StableSpectatePlugin : IPlugin
#pragma warning restore CA1001
{
    /// <summary>The stable players online (JSON object: user id → player), written by this plugin.</summary>
    public const string PlayersKey = "tomyum:stable-spectate:players";

    /// <summary>The stable players lazer is watching (JSON array of user ids), written by the spectator server.</summary>
    public const string WatchedKey = "tomyum:stable-spectate:watched";

    /// <summary>Who on lazer watches each stable player (JSON object: user id → watcher ids), written by the spectator server.</summary>
    public const string WatchersKey = "tomyum:stable-spectate:watchers";

    /// <summary>The pub/sub channel frame bundles go out on.</summary>
    public const string FramesChannel = "tomyum:stable-spectate:frames";

    private const byte Playing = 2;
    private const byte Multiplaying = 12;

    private readonly List<IDisposable> _registrations = new();
    private readonly ConcurrentDictionary<long, byte> _attachedByUs = new();
    private volatile HashSet<long> _watched = new();
    private IPluginLogger? _logger;
    private IHostPlayerView? _players;
    private IHostSpectating? _spectating;
    private IConnectionMultiplexer? _redis;
    private TimeSpan _interval = TimeSpan.FromSeconds(2);
    private bool _failing;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <inheritdoc/>
    public PluginManifest Manifest { get; } = new(
        Id: "tomyum.stable-spectate",
        Name: "Stable spectate (experimental)",
        Version: "0.1.0",
        Authors: "Phapoom Saksri",
        Description: "TumThai (osu!lazer) players see and watch osu!(stable) players (TumThai's \"Show osu!(stable) players\"), through tomyum-spectator.");

    /// <inheritdoc/>
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _logger = context.Logger;
        _players = context.GetHostService<IHostPlayerView>();
        _spectating = context.GetHostService<IHostSpectating>();
        _redis = context.GetHostService<IServiceProvider>()?.GetService(typeof(IConnectionMultiplexer)) as IConnectionMultiplexer;
        _interval = TimeSpan.FromSeconds(Math.Clamp(context.Config.Get("intervalSeconds", 2), 1, 60));

        var missing = new List<string>();
        if (_players is null)
            missing.Add("the online players (IHostPlayerView)");
        if (_spectating is null)
            missing.Add("spectating (IHostSpectating)");
        if (_redis is null)
            missing.Add("Redis");
        if (missing.Count > 0)
        {
            _logger.Warn($"{Manifest.Id}: off, no {string.Join(", ", missing)}.");
            return Task.CompletedTask;
        }

        _registrations.Add(context.Events.Subscribe<SpectateFrameEvent>(OnFrameAsync));
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        _logger.Info($"{Manifest.Id}: on (experimental).");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        foreach (var r in _registrations)
            r.Dispose();
        _registrations.Clear();

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_loop is not null)
            {
                try
                {
                    await _loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            _cts.Dispose();
            _cts = null;
        }

        foreach (var userId in _attachedByUs.Keys)
            _spectating?.DetachBot(userId);
        _attachedByUs.Clear();

        if (_redis is not null)
        {
            try
            {
                await _redis.GetDatabase().KeyDeleteAsync(PlayersKey).ConfigureAwait(false);
            }
            catch (RedisException)
            {
            }
        }
    }

    /// <summary>A frame bundle from a spectated player: passed on when lazer watches them.</summary>
    internal Task OnFrameAsync(SpectateFrameEvent e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_redis is null || !_watched.Contains(e.HostUserId))
            return Task.CompletedTask;
        if (StableFrames.Read(e.RawFrame.Span) is not { } bundle)
            return Task.CompletedTask;

        var player = _players?.FindByUserId(e.HostUserId);
        var message = JsonSerializer.Serialize(new FramesMessage(
            UserId: e.HostUserId,
            BeatmapId: player?.BeatmapId ?? 0,
            Mode: (player?.GameMode ?? 0) % 4,
            Mods: player?.Mods ?? 0,
            Action: bundle.Action,
            Frames: bundle.Frames.Select(f => new float[] { f.Time, f.X, f.Y, f.Buttons }).ToArray(),
            Score: bundle.Score));
        return _redis.GetSubscriber().PublishAsync(RedisChannel.Literal(FramesChannel), message, CommandFlags.FireAndForget);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
                if (_failing)
                    _logger?.Info($"{Manifest.Id}: working again.");
                _failing = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!_failing)
                    _logger?.Warn($"{Manifest.Id}: {ex.GetType().Name}: {ex.Message} (trying again)");
                _failing = true;
            }

            await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var db = _redis!.GetDatabase();
        var bot = _spectating!.BotName;
        var online = _players!.OnlinePlayers.Where(p => p.UserId > 0 && !string.Equals(p.Username, bot, StringComparison.OrdinalIgnoreCase)).ToList();
        var snapshot = online.ToDictionary(
            p => p.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            p => new PlayerEntry(p.Username, p.Action, IsPlaying(p.Action), p.ActionInfo, p.BeatmapId, p.GameMode % 4, p.Mods));
        await db.StringSetAsync(PlayersKey, JsonSerializer.Serialize(snapshot), TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        var watchedJson = await db.StringGetAsync(WatchedKey).ConfigureAwait(false);
        var watched = watchedJson.IsNullOrEmpty
            ? new HashSet<long>()
            : (JsonSerializer.Deserialize<long[]>(watchedJson.ToString()) ?? Array.Empty<long>()).ToHashSet();
        watched.IntersectWith(online.Select(p => p.UserId));
        _watched = watched;

        var watchersJson = await db.StringGetAsync(WatchersKey).ConfigureAwait(false);
        var watchers = watchersJson.IsNullOrEmpty
            ? new Dictionary<string, long[]>()
            : JsonSerializer.Deserialize<Dictionary<string, long[]>>(watchersJson.ToString()) ?? new Dictionary<string, long[]>();

        foreach (var userId in watched)
        {
            // The player sees who watches them from lazer (a spectator server without the watchers key: nobody).
            var want = watchers.TryGetValue(userId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var ids)
                ? ids.Where(id => id > 0 && id != userId).ToHashSet()
                : new HashSet<long>();
            foreach (var id in _spectating!.ShownWatchers(userId).Where(id => !want.Contains(id)).ToList())
                _spectating.HideWatcher(userId, id);
            var shown = false;
            foreach (var id in want)
                shown |= await _spectating.ShowWatcherAsync(userId, id, cancellationToken).ConfigureAwait(false);

            // Watched by someone on lazer: the bot spectates them (again, if osu!dan detached it), quietly when someone
            // is shown in its place.
            if (!_spectating.IsBotWatching(userId) && (shown ? _spectating.AttachBotQuietly(userId) : _spectating.AttachBot(userId)))
                _attachedByUs[userId] = 0;
            else if (!shown)
                _spectating.AttachBot(userId); // watching quietly with nobody shown any more: the player is told
        }

        // Nobody on lazer watches them any more: nobody shown.
        foreach (var player in online.Where(p => !watched.Contains(p.UserId)))
        {
            foreach (var id in _spectating!.ShownWatchers(player.UserId).ToList())
                _spectating.HideWatcher(player.UserId, id);
        }

        foreach (var userId in _attachedByUs.Keys)
        {
            if (watched.Contains(userId))
                continue;
            var player = _players.FindByUserId(userId);
            if (player is null)
            {
                _attachedByUs.TryRemove(userId, out _);
                continue;
            }

            // Not during a play: an osu!dan attempt may be using the bot too.
            if (!IsPlaying(player.Action))
            {
                _spectating!.DetachBot(userId);
                _attachedByUs.TryRemove(userId, out _);
            }
        }
    }

    private static bool IsPlaying(byte action) => action is Playing or Multiplaying;

    /// <summary>One player in <see cref="PlayersKey"/>.</summary>
    internal sealed record PlayerEntry(
        string Username, byte Action, bool Playing, string Info, long BeatmapId, int Mode, uint Mods);

    /// <summary>One message on <see cref="FramesChannel"/>.</summary>
    internal sealed record FramesMessage(
        long UserId, long BeatmapId, int Mode, uint Mods, byte Action, float[][] Frames, StableScore? Score);
}
