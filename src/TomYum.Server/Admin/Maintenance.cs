using System.Globalization;
using System.Text.Json;
using StackExchange.Redis;
using TomYum.Bancho;
using TomYum.Server.Bancho;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Admin;

/// <summary>What maintenance looks like now: on or off, the players' message, and when the lock starts.</summary>
public sealed record MaintenanceState(bool Enabled, string Message, DateTimeOffset? StartsAt, int? ByUserId, DateTimeOffset? Since)
{
    public static MaintenanceState Off { get; } = new(false, string.Empty, null, null, null);

    /// <summary>Whether players are kept out right now (on, and the countdown is over).</summary>
    public bool IsLocked(DateTimeOffset now) => Enabled && (StartsAt is null || now >= StartsAt.Value);
}

/// <summary>
/// Maintenance mode (the admin panel; <c>/_admin/maintenance</c>). While locked, players who aren't staff
/// can't log in to stable, lazer (the website refuses them; see osu-web's TomYumMaintenance) or IRC:
/// stable is told it's a server error and keeps reconnecting until maintenance ends, so it comes back
/// on its own. Score submission stays open, so plays in progress are still submitted. Turning it on
/// announces it at once (an in-game alert, and a BanchoBot message to lazer players); with a countdown,
/// the lock starts when it's over, and online stable players are then disconnected into that reconnect
/// loop. The state is kept in Redis (<c>tomyum:maintenance</c>), so a restart doesn't lift it.
/// </summary>
public sealed class Maintenance : BackgroundService
{
    private const string Key = "tomyum:maintenance";

    /// <summary>What players are told when the admin gives no message.</summary>
    public const string DefaultMessage = "production is under maintenance. You'll be reconnected automatically when it's over.";

    private readonly PlayerRegistry _players;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<Maintenance> _logger;
    private readonly IConnectionMultiplexer? _redis;
    private readonly OsuWebChatBridge? _bridge;
    private MaintenanceState _state = MaintenanceState.Off;
    private bool _lockApplied;

    public Maintenance(PlayerRegistry players, IServiceScopeFactory scopes, TimeProvider time, ILogger<Maintenance> logger,
        IConnectionMultiplexer? redis = null, OsuWebChatBridge? bridge = null)
    {
        _players = players;
        _scopes = scopes;
        _time = time;
        _logger = logger;
        _redis = redis;
        _bridge = bridge;
    }

    public MaintenanceState State => _state;

    /// <summary>Whether players who aren't staff are kept out right now.</summary>
    public bool IsLocked => _state.IsLocked(_time.GetUtcNow());

    /// <summary>The players' message (the admin's, or <see cref="DefaultMessage"/>).</summary>
    public string Notice => string.IsNullOrWhiteSpace(_state.Message) ? DefaultMessage : _state.Message;

    /// <summary>Turns maintenance on: announced now, locked after <paramref name="delay"/>.</summary>
    public async Task StartAsync(string? message, TimeSpan delay, int byUserId, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        _state = new MaintenanceState(true, (message ?? string.Empty).Trim(), delay > TimeSpan.Zero ? now + delay : null, byUserId, now);
        _lockApplied = false;
        await SaveAsync().ConfigureAwait(false);

        var when = delay > TimeSpan.Zero
            ? string.Create(CultureInfo.InvariantCulture, $"Maintenance starts in {Describe(delay)}: ")
            : "Maintenance has started: ";
        await AlertAsync(when + Notice, includeLazer: true, cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Maintenance on (by user {UserId}); locked {When}.", byUserId, delay > TimeSpan.Zero ? "in " + Describe(delay) : "now");
        ApplyLockIfDue();
    }

    /// <summary>Turns maintenance off; players come back as their clients reconnect.</summary>
    public async Task StopAsync(int byUserId)
    {
        _state = MaintenanceState.Off;
        _lockApplied = false;
        await SaveAsync().ConfigureAwait(false);
        _logger.LogWarning("Maintenance off (by user {UserId}).", byUserId);
    }

    /// <summary>
    /// An in-game alert to everyone online in stable (osu!'s yellow notification); with
    /// <paramref name="includeLazer"/>, also a BanchoBot message to everyone online in lazer.
    /// Returns how many players it reached.
    /// </summary>
    public async Task<int> AlertAsync(string text, bool includeLazer, CancellationToken cancellationToken = default)
    {
        var writer = new BanchoPacketWriter(256);
        writer.WriteStringPacket(ServerPacketId.Notification, text);
        var bytes = writer.ToArray();
        var reached = 0;
        foreach (var player in _players.All)
        {
            if (player.IsIrc)
                continue;
            player.Enqueue(bytes);
            reached++;
        }

        if (includeLazer && _bridge is not null)
        {
            using var scope = _scopes.CreateScope();
            if (scope.ServiceProvider.GetService<LazerPresence>() is { } presence
                && scope.ServiceProvider.GetService<TomYum.Domain.Repositories.IUserRepository>() is { } users)
            {
                foreach (var lazer in await presence.GetOnlineAsync().ConfigureAwait(false))
                {
                    if (await users.GetByIdAsync(lazer.UserId, cancellationToken).ConfigureAwait(false) is { } user)
                    {
                        _bridge.ForwardPrivateMessage(BanchoBot.UserId, user.Username, text);
                        reached++;
                    }
                }
            }
        }
        return reached;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadAsync().ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            ApplyLockIfDue();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>When the lock starts: everyone online who isn't staff is told and disconnected (stable then keeps reconnecting).</summary>
    private void ApplyLockIfDue()
    {
        if (_lockApplied || !IsLocked)
            return;
        _lockApplied = true;

        var writer = new BanchoPacketWriter(256);
        writer.WriteStringPacket(ServerPacketId.Notification, Notice);
        writer.WritePacket(ServerPacketId.Restart, w => w.WriteInt32(0));
        var bytes = writer.ToArray();
        var kicked = 0;
        foreach (var player in _players.All)
        {
            if (player.IsIrc || IsStaff(player.Presence.Privileges))
                continue;
            player.Enqueue(bytes);
            kicked++;
        }
        _logger.LogWarning("Maintenance lock started: {Count} player(s) disconnected.", kicked);
    }

    /// <summary>Staff in-game (the client's moderator, developer or owner badge) stay during maintenance.</summary>
    private static bool IsStaff(BanchoPrivileges privileges) =>
        (privileges & (BanchoPrivileges.Moderator | BanchoPrivileges.Developer | BanchoPrivileges.Owner)) != 0;

    private async Task SaveAsync()
    {
        if (_redis is null)
            return;
        var db = _redis.GetDatabase();
        if (_state.Enabled)
            await db.StringSetAsync(Key, JsonSerializer.Serialize(_state)).ConfigureAwait(false);
        else
            await db.KeyDeleteAsync(Key).ConfigureAwait(false);
    }

    private async Task LoadAsync()
    {
        if (_redis is null)
            return;
        try
        {
            var stored = await _redis.GetDatabase().StringGetAsync(Key).ConfigureAwait(false);
            if (stored.HasValue && JsonSerializer.Deserialize<MaintenanceState>(stored.ToString()) is { Enabled: true } state)
            {
                _state = state;
                _logger.LogWarning("Maintenance is on (kept from before the restart).");
            }
        }
#pragma warning disable CA1031 // a broken stored state means "off", not a failed start
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "The stored maintenance state couldn't be read; maintenance is off.");
        }
    }

    private static string Describe(TimeSpan delay) =>
        delay.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(delay.TotalMinutes)} minute{(Math.Round(delay.TotalMinutes) == 1 ? string.Empty : "s")}")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(delay.TotalSeconds)} seconds");
}
