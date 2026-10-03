// Metrics plugin — entry point: the /metrics route, the event counters and the database poller.
//
// Serves Prometheus numbers for the status page (~/workspace/status-page, job "tomyum"). Lives in a
// plugin so core TomYum stays free of it; it needs only TomYum.Plugins.Abstractions:
//   - players online by client: IHostOnlineCounts (stable, IRC, lazer from the spectator feed);
//   - stable scores: ScoreSubmittingEvent (the last step before a score is stored);
//   - stable logins: PlayerLoginEvent (ok) and PlayerLoginRefusedEvent (failed / held);
//   - lazer scores and registered players: the website's database, through IPluginContext.Data
//     (so the plugin must be listed in Plugins:Trusted; without it those two are left out).
//
// Human docs: plugins/tomyum-metrics/README.md.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using TomYum.Plugins.Abstractions;

namespace TomYum.Plugins.Metrics;

/// <summary>The Prometheus metrics plugin: <c>GET /metrics</c> on internal host names only.</summary>
#pragma warning disable CA1001 // _cts is disposed in ShutdownAsync, the end of a plugin's life
public sealed class MetricsPlugin : IPlugin
#pragma warning restore CA1001
{
    /// <summary>The host names (with port) <c>/metrics</c> answers on unless <c>hosts</c> is set.</summary>
    public const string DefaultHosts = "127.0.0.1:8091,localhost:8091,server:8080,localhost:8080";

    private static readonly Regex DatabaseName = new("^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant);

    private readonly List<IDisposable> _registrations = new();
    private readonly MetricsCounters _counters = new();
    private IPluginLogger? _logger;
    private IHostOnlineCounts? _online;
    private IPluginDataAccess? _data;
    private HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private string? _osuWebDatabase;
    private TimeSpan _interval = TimeSpan.FromSeconds(15);
    private TimeSpan _usersInterval = TimeSpan.FromMinutes(5);
    private CancellationTokenSource? _cts;
    private Task? _poller;

    // Poller state (only touched by the poller task).
    private ulong? _lastScoreId;
    private DateTimeOffset _usersCountedAt = DateTimeOffset.MinValue;
    private bool _failing;

    public PluginManifest Manifest { get; } = new(
        Id: "tomyum.metrics",
        Name: "Metrics (Prometheus)",
        Version: "0.1.0",
        Authors: "Phapoom Saksri",
        Description: "GET /metrics for the status page's Prometheus. Internal host names only.");

    /// <summary>The counters and gauges this plugin serves (exposed for tests).</summary>
    public MetricsCounters Counters => _counters;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        _logger = context.Logger;
        _online = context.GetHostService<IHostOnlineCounts>();
        _data = context.Data;

        var hosts = context.Config.Get("hosts", DefaultHosts) ?? DefaultHosts;
        _hosts = new HashSet<string>(
            hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        _interval = TimeSpan.FromSeconds(Math.Max(5, context.Config.Get("intervalSeconds", 15)));
        _usersInterval = TimeSpan.FromMinutes(Math.Max(1, context.Config.Get("usersIntervalMinutes", 5)));

        var database = context.Config.Get("osuWebDatabase", "osu") ?? string.Empty;
        if (database.Length == 0)
            _osuWebDatabase = null;
        else if (DatabaseName.IsMatch(database))
            _osuWebDatabase = database;
        else
            _logger.Error($"tomyum.metrics: osuWebDatabase '{database}' isn't a plain database name; lazer scores and registered players are off.");

        _registrations.Add(context.Http.Map("GET", "/metrics", ServeAsync));
        _registrations.Add(context.Events.Subscribe<ScoreSubmittingEvent>((e, _) =>
        {
            // A plugin that runs after this one could still veto the score; vetoes before it stop here.
            if (!e.IsVetoed)
                _counters.ScoreSubmitted(ScoreClient.Stable);
            return Task.CompletedTask;
        }));
        _registrations.Add(context.Events.Subscribe<PlayerLoginEvent>((_, _) =>
        {
            _counters.Login(LoginResult.Ok);
            return Task.CompletedTask;
        }));
        _registrations.Add(context.Events.Subscribe<PlayerLoginRefusedEvent>((e, _) =>
        {
            _counters.Login(e.Held ? LoginResult.Held : LoginResult.Failed);
            return Task.CompletedTask;
        }));

        if (_online is null)
            _logger.Warn("tomyum.metrics: the host doesn't publish IHostOnlineCounts; players online are left out.");

        if (_data is null)
        {
            _logger.Warn(
                "tomyum.metrics: no database access, so lazer scores and registered players are left out. "
                + "Add \"tomyum.metrics\" to Plugins:Trusted (and enable the database) to get them.");
        }
        else if (_osuWebDatabase is not null)
        {
            _cts = new CancellationTokenSource();
            _poller = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);
        }

        _logger.Info($"tomyum.metrics: GET /metrics on {string.Join(", ", _hosts)}");
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        foreach (var registration in _registrations)
            registration.Dispose();
        _registrations.Clear();

        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            if (_poller is not null)
            {
                try
                {
                    await _poller.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            _cts.Dispose();
            _cts = null;
            _poller = null;
        }
    }

    private async Task<PluginHttpResponse> ServeAsync(PluginHttpRequest request, CancellationToken cancellationToken)
    {
        // Plugin routes match every host, so refuse the public ones here: only the localhost port the
        // status page's Prometheus scrapes (and the compose network name) get the numbers.
        if (!request.Headers.TryGetValue("Host", out var host) || !_hosts.Contains(host))
            return Text(404, "not found");

        HostOnlineCounts? online = null;
        if (_online is not null)
        {
            try
            {
                online = await _online.GetAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.Warn($"tomyum.metrics: counting players online failed: {ex.Message}");
            }
        }

        var body = PrometheusText.Render(_counters, online);
        return new PluginHttpResponse(200, "text/plain; version=0.0.4; charset=utf-8", Encoding.UTF8.GetBytes(body));
    }

    private static PluginHttpResponse Text(int status, string text) =>
        new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                await PollAsync(cancellationToken).ConfigureAwait(false);
                if (_failing)
                    _logger?.Info("tomyum.metrics: reading the database works again.");
                _failing = false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Logged once per outage, not every pass; tried again on the next tick.
                if (!_failing)
                    _logger?.Warn($"tomyum.metrics: reading the database failed; trying again every {_interval.TotalSeconds}s. {ex.Message}");
                _failing = true;
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>One pass: lazer scores since the last pass and, every few minutes, registered players.</summary>
    private async Task PollAsync(CancellationToken cancellationToken)
    {
        if (_data is null || _osuWebDatabase is null)
            return;

        var db = "`" + _osuWebDatabase + "`";
        var connection = await _data.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            // Lazer scores: rows the website's `scores` got since the last pass. Stable plays mirrored
            // there carry a legacy_score_id. The first pass only sets the starting point, so a restart
            // doesn't count old plays again.
            var newest = ToUInt64(await ScalarAsync(connection, $"SELECT MAX(id) FROM {db}.scores", null, cancellationToken).ConfigureAwait(false));
            if (_lastScoreId is { } last && newest > last)
            {
                var lazer = ToUInt64(await ScalarAsync(
                    connection,
                    $"SELECT COUNT(*) FROM {db}.scores WHERE id > @last AND id <= @newest AND legacy_score_id IS NULL",
                    new Dictionary<string, object> { ["@last"] = last, ["@newest"] = newest },
                    cancellationToken).ConfigureAwait(false));
                _counters.ScoreSubmitted(ScoreClient.Lazer, (long)lazer);
            }
            _lastScoreId = newest;

            var now = DateTimeOffset.UtcNow;
            if (now - _usersCountedAt < _usersInterval)
                return;

            // Registered players: not inactive (user_type 1), not restricted (user_warnings), not
            // BanchoBot (id 1), and not a chat bot account when TomYum's bot table is there.
            var hasBots = ToUInt64(await ScalarAsync(
                connection,
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = @db AND table_name = 'tomyum_bot_accounts'",
                new Dictionary<string, object> { ["@db"] = _osuWebDatabase },
                cancellationToken).ConfigureAwait(false)) > 0;
            var sql = $"SELECT COUNT(*) FROM {db}.phpbb_users u WHERE u.user_type <> 1 AND u.user_warnings = 0 AND u.user_id <> 1"
                + (hasBots ? $" AND NOT EXISTS (SELECT 1 FROM {db}.tomyum_bot_accounts b WHERE b.user_id = u.user_id)" : string.Empty);
            _counters.RegisteredUsers = (long)ToUInt64(await ScalarAsync(connection, sql, null, cancellationToken).ConfigureAwait(false));
            _usersCountedAt = now;
        }
    }

    private static async Task<object?> ScalarAsync(
        DbConnection connection, string sql, IReadOnlyDictionary<string, object>? parameters, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            if (parameters is not null)
            {
                foreach (var (name, value) in parameters)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = value;
                    command.Parameters.Add(parameter);
                }
            }

            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static ulong ToUInt64(object? value) =>
        value is null || value is DBNull ? 0 : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
}
