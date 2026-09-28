using System.Globalization;
using System.Text.Json;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;

namespace TomYum.Server.Discord;

/// <summary>
/// The bot's clockwork, once a minute:
/// <list type="bullet">
/// <item>the status message: pinned in <see cref="DiscordOptions.StatusChannelId"/> and edited in place
/// (who's online on each client, plays and players today, uptime), and the bot's own status line;</item>
/// <item>the map of the day: a random ranked map each UTC day; the best pass on it that day (by pp,
/// then score) wins the Map of the Day role for a week, if the winner linked their Discord;</item>
/// <item>lazer's daily challenge: announced when osu-web opens the day's room, with the top
/// players once it closes.</item>
/// </list>
/// What it posted is remembered (<see cref="DiscordState"/>), so restarts don't post again.
/// </summary>
public sealed class DiscordDaily : BackgroundService
{
    public const string RoleName = "Map of the Day";

    public static readonly TimeSpan HoldFor = TimeSpan.FromDays(7);

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly DiscordOptions _options;
    private readonly IDiscordGateway _gateway;
    private readonly DiscordFormat _format;
    private readonly DiscordState _state;
    private readonly IServiceScopeFactory _scopes;
    private readonly MatchRegistry _matches;
    private readonly DiscordCommands _commands;
    private readonly TomYumVersion _version;
    private readonly TimeProvider _time;
    private readonly ILogger<DiscordDaily> _logger;
    private readonly OsuWeb.OnlinePlayers? _online;
    private readonly DateTimeOffset _started;

    public DiscordDaily(
        DiscordOptions options, IDiscordGateway gateway, DiscordFormat format, DiscordState state, IServiceScopeFactory scopes,
        MatchRegistry matches, DiscordCommands commands, TomYumVersion version, TimeProvider time, ILogger<DiscordDaily> logger,
        OsuWeb.OnlinePlayers? online = null)
    {
        _options = options;
        _gateway = gateway;
        _format = format;
        _state = state;
        _scopes = scopes;
        _matches = matches;
        _commands = commands;
        _version = version;
        _time = time;
        _logger = logger;
        _online = online;
        _started = time.GetUtcNow();
    }

    /// <summary>One pass (does nothing while Discord is unreachable).</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (!_gateway.IsConnected)
            return;
        using var scope = _scopes.CreateScope();
        if (scope.ServiceProvider.GetService<OsuWebCommunity>() is not { } community)
            return;

        await StatusAsync(community, cancellationToken).ConfigureAwait(false);
        await MapOfTheDayAsync(scope.ServiceProvider, community, cancellationToken).ConfigureAwait(false);
        if (scope.ServiceProvider.GetService<OsuWebDailyChallenge>() is { } challenge)
            await DailyChallengeAsync(scope.ServiceProvider, community, challenge, cancellationToken).ConfigureAwait(false);
    }

    // ── lazer's daily challenge ───────────────────────────────────────────────────────────

    /// <summary>The room last announced, and whether its results were posted.</summary>
    private sealed record AnnouncedChallenge(ulong RoomId, string Name, bool Settled);

    private async Task DailyChallengeAsync(IServiceProvider services, OsuWebCommunity community, OsuWebDailyChallenge challenge, CancellationToken cancellationToken)
    {
        var last = await _state.GetAsync("daily-challenge").ConfigureAwait(false) is { } json ? JsonSerializer.Deserialize<AnnouncedChallenge>(json) : null;
        var current = await challenge.CurrentAsync(cancellationToken).ConfigureAwait(false);

        if (last is { Settled: false } && last.RoomId != current?.RoomId)
        {
            await _gateway.PostAsync(_options.MapOfTheDayChannel, await ChallengeResultsAsync(challenge, last, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            last = last with { Settled = true };
            await _state.SetAsync("daily-challenge", JsonSerializer.Serialize(last)).ConfigureAwait(false);
        }
        if (current is null || current.RoomId == last?.RoomId)
            return;

        var card = await _commands.MapCardAsync(services, community, (int)current.BeatmapId, TomYum.Bancho.Mods.None, current.RulesetId, cancellationToken).ConfigureAwait(false);
        var required = OsuWebDailyChallenge.Acronyms(current.RequiredMods);
        var allowed = OsuWebDailyChallenge.Acronyms(current.AllowedMods);
        var mods = (required.Count > 0 ? $"Required mods: **{string.Join(string.Empty, required)}**. " : string.Empty)
                   + (allowed.Count > 0 ? $"Allowed: {string.Join(' ', allowed)}." : "No mods.");
        var ends = current.EndsAt is { } end ? $" Open until {DiscordFormat.Relative(DateTime.SpecifyKind(end, DateTimeKind.Utc))}." : string.Empty;
        await _gateway.PostAsync(_options.MapOfTheDayChannel, new DiscordPost(
            $"📅 **{DiscordFormat.Escape(current.Name)}** is open in osu!lazer (main menu → Daily Challenge). Play it as often as you like: your best score counts.{ends}\n{mods}",
            card), cancellationToken).ConfigureAwait(false);
        await _state.SetAsync("daily-challenge", JsonSerializer.Serialize(new AnnouncedChallenge(current.RoomId, current.Name, false))).ConfigureAwait(false);
    }

    private async Task<DiscordPost> ChallengeResultsAsync(OsuWebDailyChallenge challenge, AnnouncedChallenge room, CancellationToken cancellationToken)
    {
        var results = await challenge.ResultsAsync(room.RoomId, 5, cancellationToken).ConfigureAwait(false);
        if (results.Count == 0)
            return new DiscordPost($"📅 Nobody passed **{DiscordFormat.Escape(room.Name)}**.");

        var players = await challenge.PlayersAsync(room.RoomId, cancellationToken).ConfigureAwait(false);
        string[] medals = ["🥇", "🥈", "🥉", "4.", "5."];
        var lines = results.Select((r, i) => string.Create(CultureInfo.InvariantCulture,
            $"{medals[i]} [{DiscordFormat.LinkLabel(r.Username)}]({_format.Profile((int)r.UserId)}) ▸ {DiscordFormat.Number(r.TotalScore)} ▸ {DiscordFormat.Percent(r.Accuracy)}"));
        return new DiscordPost(Embed: new DiscordEmbed
        {
            Title = $"{room.Name}: results",
            Url = string.Create(CultureInfo.InvariantCulture, $"{_format.Site}/multiplayer/rooms/{room.RoomId}"),
            Description = string.Join('\n', lines),
            Color = DiscordFormat.Gold,
            Footer = string.Create(CultureInfo.InvariantCulture, $"{players} player(s) took part"),
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // one bad pass (database hiccup) must not stop the clock
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "Discord: the status message or map of the day failed; trying again shortly.");
            }
            await Task.Delay(Interval, _time, stoppingToken).ConfigureAwait(false);
        }
    }

    // ── status ────────────────────────────────────────────────────────────────────────────

    private async Task StatusAsync(OsuWebCommunity community, CancellationToken cancellationToken)
    {
        var online = _online is null ? [] : await _online.GetAsync(cancellationToken).ConfigureAwait(false);
        var inGame = online.Count(p => p.Client != OsuWeb.OnlineClient.Website);
        await _gateway.SetActivityAsync(inGame == 1 ? "1 player on " + SiteHost() : string.Create(CultureInfo.InvariantCulture, $"{inGame} players on {SiteHost()}"), cancellationToken).ConfigureAwait(false);

        if (_options.StatusChannelId == 0)
            return;
        var numbers = await community.GetServerNumbersAsync(cancellationToken).ConfigureAwait(false);
        var post = StatusPost(online, numbers);

        var key = "status-message:" + _options.StatusChannelId.ToString(CultureInfo.InvariantCulture);
        if (await _state.GetAsync(key).ConfigureAwait(false) is { } stored
            && ulong.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var messageId)
            && await _gateway.EditAsync(_options.StatusChannelId, messageId, post, cancellationToken).ConfigureAwait(false))
            return;

        // None yet, or it was deleted: post a new one and pin it.
        if (await _gateway.SendAsync(_options.StatusChannelId, post, cancellationToken).ConfigureAwait(false) is { } sent)
        {
            await _gateway.PinAsync(_options.StatusChannelId, sent, cancellationToken).ConfigureAwait(false);
            await _state.SetAsync(key, sent.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }
    }

    /// <summary>The status message.</summary>
    public DiscordPost StatusPost(IReadOnlyList<OsuWeb.OnlineEntry> online, ServerNumbers numbers)
    {
        ArgumentNullException.ThrowIfNull(online);
        ArgumentNullException.ThrowIfNull(numbers);
        int Count(OsuWeb.OnlineClient client) => online.Count(p => p.Client == client);
        var playing = online.Count(p => p.Playing);
        var uptime = _time.GetUtcNow() - _started;
        var now = _time.GetUtcNow();
        return new DiscordPost(Embed: new DiscordEmbed
        {
            Title = $"🟢 {SiteHost()} is online",
            Url = _format.Site,
            Description = string.Create(CultureInfo.InvariantCulture,
                $"**{online.Count}** online now ▸ **{playing}** playing ▸ **{_matches.Count}** multiplayer room(s)"),
            Fields =
            [
                new DiscordField("osu!stable", DiscordFormat.Number(Count(OsuWeb.OnlineClient.Stable))),
                new DiscordField("osu!lazer", DiscordFormat.Number(Count(OsuWeb.OnlineClient.Lazer))),
                new DiscordField("Website", DiscordFormat.Number(Count(OsuWeb.OnlineClient.Website))),
                new DiscordField("Plays (24h)", DiscordFormat.Number(numbers.PlaysToday)),
                new DiscordField("Peak online (24h)", DiscordFormat.Number(numbers.PeakOnlineToday)),
                new DiscordField("New players (24h)", DiscordFormat.Number(numbers.NewPlayersToday)),
                new DiscordField("Players", DiscordFormat.Number(numbers.Players)),
                new DiscordField("Ranked sets", DiscordFormat.Number(numbers.RankedSets)),
                new DiscordField("Uptime", Uptime(uptime)),
            ],
            Color = 0x57F287,
            Footer = $"TomYum {_version.Display} ▸ updated every minute",
            Timestamp = now,
        });
    }

    private static string Uptime(TimeSpan span) =>
        span.TotalDays >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays}d {span.Hours}h")
        : span.TotalHours >= 1 ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h {span.Minutes}m")
        : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}m");

    private string SiteHost() => Uri.TryCreate(_format.Site, UriKind.Absolute, out var site) ? site.Host : _format.Site;

    // ── map of the day ────────────────────────────────────────────────────────────────────

    /// <summary>Today's map (UTC day) and whether its winner was settled.</summary>
    private sealed record MapOfTheDay(string Day, int BeatmapId, int Ruleset, bool Settled);

    private async Task MapOfTheDayAsync(IServiceProvider services, OsuWebCommunity community, CancellationToken cancellationToken)
    {
        var today = _time.GetUtcNow().UtcDateTime.Date;
        var dayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var current = await _state.GetAsync("motd").ConfigureAwait(false) is { } json ? JsonSerializer.Deserialize<MapOfTheDay>(json) : null;

        if (current is not null && current.Day != dayKey && !current.Settled)
        {
            await SettleAsync(services, community, current, cancellationToken).ConfigureAwait(false);
            current = current with { Settled = true };
            await _state.SetAsync("motd", JsonSerializer.Serialize(current)).ConfigureAwait(false);
        }
        if (current?.Day != dayKey)
        {
            if (await community.GetRandomRankedMapAsync(3.0, 6.5, cancellationToken).ConfigureAwait(false) is { } beatmapId
                && await community.GetMapAsync(beatmapId, cancellationToken).ConfigureAwait(false) is { } map)
            {
                var card = await _commands.MapCardAsync(services, community, beatmapId, TomYum.Bancho.Mods.None, null, cancellationToken).ConfigureAwait(false);
                await _gateway.PostAsync(_options.MapOfTheDayChannel, new DiscordPost(
                    "🗺️ **Map of the day!** The best pass on it by midnight UTC (most pp, then score) wins the **Map of the Day** role for a week. Link your account with /link to get it.",
                    card), cancellationToken).ConfigureAwait(false);
                await _state.SetAsync("motd", JsonSerializer.Serialize(new MapOfTheDay(dayKey, beatmapId, map.Ruleset, false))).ConfigureAwait(false);
            }
        }

        await ExpireHoldersAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SettleAsync(IServiceProvider services, OsuWebCommunity community, MapOfTheDay day, CancellationToken cancellationToken)
    {
        var from = DateTime.SpecifyKind(DateTime.ParseExact(day.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var best = await community.GetBestBetweenAsync(day.BeatmapId, day.Ruleset, from, from.AddDays(1), cancellationToken).ConfigureAwait(false);
        if (best is null)
        {
            await _gateway.PostAsync(_options.MapOfTheDayChannel, new DiscordPost("🗺️ Nobody passed yesterday's map of the day. Today's is coming up!"), cancellationToken).ConfigureAwait(false);
            return;
        }

        var link = await services.GetRequiredService<TomYum.Domain.Repositories.IDiscordLinkRepository>().GetByUserIdAsync(best.UserId, cancellationToken).ConfigureAwait(false);
        var role = link is null ? 0 : await RoleAsync(cancellationToken).ConfigureAwait(false);
        var given = link is not null && role != 0 && await _gateway.SetRoleAsync(link.DiscordId, role, give: true, cancellationToken).ConfigureAwait(false);
        if (given)
        {
            await _state.SetFieldAsync("motd-holders", link!.DiscordId.ToString(CultureInfo.InvariantCulture),
                (_time.GetUtcNow() + HoldFor).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }

        var winner = link is null ? $"**{DiscordFormat.Escape(best.Username)}**" : $"<@{link.DiscordId.ToString(CultureInfo.InvariantCulture)}>";
        var prize = given ? " They hold the **Map of the Day** role for a week." : link is null ? " (Link your Discord with /link to get the role next time.)" : string.Empty;
        await _gateway.PostAsync(_options.MapOfTheDayChannel, new DiscordPost(Embed: new DiscordEmbed
        {
            AuthorName = best.Username,
            AuthorUrl = _format.Profile(best.UserId),
            AuthorIconUrl = _format.Avatar(best.UserId),
            Title = DiscordFormat.ScoreTitle(best),
            Url = _format.Beatmap(best.BeatmapId),
            Description = $"🏆 {winner} won yesterday's map of the day!{prize}\n{DiscordFormat.ScoreDetails(best)}",
            ThumbnailUrl = _format.ListCover(best.BeatmapsetId),
            Color = DiscordFormat.Gold,
        }), cancellationToken).ConfigureAwait(false);
    }

    private async Task ExpireHoldersAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        foreach (var (member, until) in await _state.GetFieldsAsync("motd-holders").ConfigureAwait(false))
        {
            if (!long.TryParse(until, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiry) || expiry > now)
                continue;
            var role = await RoleAsync(cancellationToken).ConfigureAwait(false);
            if (ulong.TryParse(member, NumberStyles.None, CultureInfo.InvariantCulture, out var discordId) && role != 0)
                await _gateway.SetRoleAsync(discordId, role, give: false, cancellationToken).ConfigureAwait(false);
            await _state.RemoveFieldAsync("motd-holders", member).ConfigureAwait(false);
        }
    }

    /// <summary>The configured role, else the one the bot made (made once, remembered).</summary>
    private async Task<ulong> RoleAsync(CancellationToken cancellationToken)
    {
        if (_options.MapOfTheDayRoleId != 0)
            return _options.MapOfTheDayRoleId;
        if (await _state.GetAsync("motd-role").ConfigureAwait(false) is { } stored && ulong.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return id;
        var made = await _gateway.EnsureRoleAsync(RoleName, DiscordFormat.Gold, cancellationToken).ConfigureAwait(false);
        if (made != 0)
            await _state.SetAsync("motd-role", made.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        return made;
    }
}
