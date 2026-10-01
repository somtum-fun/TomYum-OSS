using System.Globalization;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Admin;
using TomYum.Server.Bancho;

namespace TomYum.Server.Discord;

/// <summary>
/// Everything the bot posts on its own, read from the databases every few seconds so stable and
/// lazer (and the website's staff panels) are covered alike:
/// <list type="bullet">
/// <item>public log: new #1s, big plays (<see cref="DiscordOptions.BigPlayPp"/>), medals, new players;</item>
/// <item>beatmaps: ranked, approved, qualified and loved maps;</item>
/// <item>user log: staff actions on accounts, blocked ban evasion, Discord links;</item>
/// <item>server log: other staff actions, the server starting, and errors.</item>
/// </list>
/// It also keeps linked members' roles in line with their website groups. Only what happens while
/// it runs is announced (it starts from the newest rows); lazer scores wait for ppy's processor so
/// their pp is final.
/// </summary>
public sealed class DiscordAnnouncer : BackgroundService
{
    /// <summary>How long a lazer score may wait for the score processor before it's judged anyway.</summary>
    public static readonly TimeSpan ProcessingGrace = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RoleSyncInterval = TimeSpan.FromMinutes(30);

    private static readonly HashSet<string> AnnouncedStatuses = new(StringComparer.OrdinalIgnoreCase) { "ranked", "approved", "qualified", "loved" };

    private readonly DiscordOptions _options;
    private readonly IDiscordGateway _gateway;
    private readonly DiscordFormat _format;
    private readonly DiscordLogSink _errors;
    private readonly IServiceScopeFactory _scopes;
    private readonly DiscordLinkCodes _codes;
    private readonly TomYumVersion _version;
    private readonly TimeProvider _time;
    private readonly ILogger<DiscordAnnouncer> _logger;
    private readonly DiscordWatch? _watch;

    private bool _started;
    private ulong _scoreMark;
    private DateTime _medalMark;
    private HashSet<(int UserId, int MedalId)> _medalsAtMark = [];
    private int _userMark;
    private long _staffMark;
    private DateTimeOffset _nextRoleSync;

    public DiscordAnnouncer(
        DiscordOptions options,
        IDiscordGateway gateway,
        DiscordFormat format,
        DiscordLogSink errors,
        IServiceScopeFactory scopes,
        DiscordLinkCodes codes,
        TomYumVersion version,
        TimeProvider time,
        ILogger<DiscordAnnouncer> logger,
        DiscordWatch? watch = null)
    {
        _watch = watch;
        _options = options;
        _gateway = gateway;
        _format = format;
        _errors = errors;
        _scopes = scopes;
        _codes = codes;
        _version = version;
        _time = time;
        _logger = logger;
        _codes.Linked += OnLinkedAsync;
        _codes.Unlinked += OnUnlinkedAsync;
    }

    public override void Dispose()
    {
        _codes.Linked -= OnLinkedAsync;
        _codes.Unlinked -= OnUnlinkedAsync;
        base.Dispose();
    }

    /// <summary>One pass: posts what's new since the last pass. Does nothing while Discord is unreachable.</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (!_gateway.IsConnected)
            return;

        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        var community = services.GetService<OsuWebCommunity>();
        var staffLog = services.GetService<IStaffLogRepository>();

        if (!_started)
        {
            await StartAsync(community, staffLog, cancellationToken).ConfigureAwait(false);
            _started = true;
        }

        if (community is not null)
        {
            await AnnounceScoresAsync(community, cancellationToken).ConfigureAwait(false);
            await AnnounceMedalsAsync(community, cancellationToken).ConfigureAwait(false);
            await AnnouncePlayersAsync(community, cancellationToken).ConfigureAwait(false);
            if (_watch is not null)
                await _watch.TickAsync(community, cancellationToken).ConfigureAwait(false);
        }
        if (staffLog is not null)
            await AnnounceStaffLogAsync(staffLog, community, services, cancellationToken).ConfigureAwait(false);

        foreach (var error in _errors.Drain(5))
            await _gateway.PostAsync(_options.ServerLogChannelId, new DiscordPost(error), cancellationToken).ConfigureAwait(false);

        if (_time.GetUtcNow() >= _nextRoleSync)
        {
            _nextRoleSync = _time.GetUtcNow() + RoleSyncInterval;
            foreach (var link in await services.GetRequiredService<IDiscordLinkRepository>().ListAsync(cancellationToken).ConfigureAwait(false))
                await SyncRolesAsync(services, link.UserId, link.DiscordId, cancellationToken).ConfigureAwait(false);
        }
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
#pragma warning disable CA1031 // one bad pass (database hiccup) must not stop the announcements
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "Discord announcements: a pass failed; trying again shortly.");
            }
            await Task.Delay(Interval, _time, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Starts every feed at the newest row, and says the server is up.</summary>
    private async Task StartAsync(OsuWebCommunity? community, IStaffLogRepository? staffLog, CancellationToken cancellationToken)
    {
        if (community is not null)
        {
            _scoreMark = await community.GetLatestScoreIdAsync(cancellationToken).ConfigureAwait(false);
            _medalMark = await community.GetLatestMedalTimeAsync(cancellationToken).ConfigureAwait(false);
            _medalsAtMark = (await community.GetMedalsSinceAsync(_medalMark, cancellationToken).ConfigureAwait(false))
                .Select(m => (m.UserId, m.MedalId)).ToHashSet();
            _userMark = await community.GetLatestUserIdAsync(cancellationToken).ConfigureAwait(false);
            if (_watch is not null)
                await _watch.StartAsync(community, cancellationToken).ConfigureAwait(false);
        }
        if (staffLog is not null)
            _staffMark = await staffLog.ListAsync(new StaffLogQuery(Limit: 1), cancellationToken).ConfigureAwait(false) is [var newest, ..] ? newest.Id : 0;

        await _gateway.PostAsync(_options.ServerLogChannelId, new DiscordPost(Embed: new DiscordEmbed
        {
            Description = $"🟢 **TomYum {_version.Display}** is up.",
            Color = 0x57F287,
            Timestamp = _time.GetUtcNow(),
        }), cancellationToken).ConfigureAwait(false);
    }

    private async Task AnnounceScoresAsync(OsuWebCommunity community, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var score in await community.GetScoresAfterAsync(_scoreMark, 100, cancellationToken).ConfigureAwait(false))
        {
            // Wait (in order) for lazer scores until the processor has given them their pp.
            if (!score.IsStable && now - score.EndedAt < ProcessingGrace
                && !await community.IsProcessedAsync(score.ScoreId, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
            _scoreMark = score.ScoreId;
            if (!score.Passed)
                continue;

            var first = await community.TookFirstPlaceAsync(score, cancellationToken).ConfigureAwait(false);
            var big = score.Pp >= _options.BigPlayPp && await community.IsPersonalBestPpAsync(score, cancellationToken).ConfigureAwait(false);
            if (first || big)
                await _gateway.PostAsync(_options.PublicLogChannelId, ScorePost(score, first, big), cancellationToken).ConfigureAwait(false);
            if (_watch is not null)
                await WatchAsync(community, score, first, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The suspicious-play checks, skipped for players on the anti-cheat whitelist. What they flag is
    /// reported to staff by the watch itself (the reports channel), not again in the staff log.
    /// </summary>
    private async Task WatchAsync(OsuWebCommunity community, ScoreCard score, bool first, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var flags = scope.ServiceProvider.GetService<TomYum.Persistence.OsuWeb.OsuWebAccountFlags>();
        var whitelisted = flags is not null && await flags.IsWhitelistedAsync(score.UserId, cancellationToken).ConfigureAwait(false);
        await _watch!.OnScoreAsync(community, score, first, cancellationToken, whitelisted).ConfigureAwait(false);
    }

    /// <summary>The announcement for a new #1 and/or big play.</summary>
    public DiscordPost ScorePost(ScoreCard score, bool first, bool big)
    {
        ArgumentNullException.ThrowIfNull(score);
        var headline = (first, big) switch
        {
            (true, true) => $"🥇 **New #1** and a **{DiscordFormat.Pp(score.Pp ?? 0)}** play!",
            (true, false) => "🥇 **New #1!**",
            _ => $"🔥 **{DiscordFormat.Pp(score.Pp ?? 0)}** play!",
        };
        return new DiscordPost(Embed: new DiscordEmbed
        {
            AuthorName = score.Username,
            AuthorUrl = _format.Profile(score.UserId),
            AuthorIconUrl = _format.Avatar(score.UserId),
            Title = DiscordFormat.ScoreTitle(score),
            Url = _format.Beatmap(score.BeatmapId),
            Description = $"{headline}\n{DiscordFormat.ScoreDetails(score)}",
            ThumbnailUrl = _format.ListCover(score.BeatmapsetId),
            Color = first ? DiscordFormat.Gold : DiscordFormat.Accent,
            Footer = $"{DiscordFormat.Ruleset(score.Ruleset)} ▸ {(score.IsStable ? "stable" : "lazer")}",
            Timestamp = new DateTimeOffset(score.EndedAt, TimeSpan.Zero),
        });
    }

    private async Task AnnounceMedalsAsync(OsuWebCommunity community, CancellationToken cancellationToken)
    {
        foreach (var medal in await community.GetMedalsSinceAsync(_medalMark, cancellationToken).ConfigureAwait(false))
        {
            if (medal.UnlockedAt == _medalMark && _medalsAtMark.Contains((medal.UserId, medal.MedalId)))
                continue;
            if (medal.UnlockedAt > _medalMark)
            {
                _medalMark = medal.UnlockedAt;
                _medalsAtMark = [];
            }
            _medalsAtMark.Add((medal.UserId, medal.MedalId));

            await _gateway.PostAsync(_options.PublicLogChannelId, new DiscordPost(Embed: new DiscordEmbed
            {
                AuthorName = medal.Username,
                AuthorUrl = _format.Profile(medal.UserId),
                AuthorIconUrl = _format.Avatar(medal.UserId),
                Description = string.IsNullOrWhiteSpace(medal.Description)
                    ? $"🏅 Unlocked **{DiscordFormat.Escape(medal.Name)}**"
                    : $"🏅 Unlocked **{DiscordFormat.Escape(medal.Name)}**\n*{DiscordFormat.Escape(StripTags(medal.Description))}*",
                Color = DiscordFormat.Gold,
            }), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AnnouncePlayersAsync(OsuWebCommunity community, CancellationToken cancellationToken)
    {
        foreach (var player in await community.GetPlayersAfterAsync(_userMark, cancellationToken).ConfigureAwait(false))
        {
            _userMark = player.UserId;
            await _gateway.PostAsync(_options.PublicLogChannelId, new DiscordPost(Embed: new DiscordEmbed
            {
                AuthorName = player.Username,
                AuthorUrl = _format.Profile(player.UserId),
                AuthorIconUrl = _format.Avatar(player.UserId),
                Description = $"👋 Welcome, **{DiscordFormat.Escape(player.Username)}** {DiscordFormat.Flag(player.Country)}!",
                Color = DiscordFormat.Accent,
            }), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AnnounceStaffLogAsync(IStaffLogRepository staffLog, OsuWebCommunity? community, IServiceProvider services, CancellationToken cancellationToken)
    {
        var entries = (await staffLog.ListAsync(new StaffLogQuery(Limit: 100), cancellationToken).ConfigureAwait(false))
            .Where(e => e.Id > _staffMark)
            .OrderBy(e => e.Id)
            .ToList();
        var users = services.GetRequiredService<IUserRepository>();
        foreach (var entry in entries)
        {
            _staffMark = entry.Id;
            var actor = entry.ActorId > 0 ? await users.GetByIdAsync(entry.ActorId, cancellationToken).ConfigureAwait(false) : null;
            if (entry.Action is BanchoLoginHandler.SharedMachineAction or Anticheat.ClientFlags.RegistryAction && entry.TargetId is { } suspect)
            {
                // An alert for staff to act on: the reports channel, with the moderation buttons.
                var reason = entry.Action == BanchoLoginHandler.SharedMachineAction ? "Same machine as a restricted account" : "hq!osu traces reported by the osu! client";
                await _gateway.PostAsync(_options.ReportsChannel, StaffPost(entry, actor) with
                {
                    Buttons = _format.ModerationButtons((int)suspect, reason, (int)suspect),
                }, cancellationToken).ConfigureAwait(false);
                continue;
            }
            var channel = entry.Action.StartsWith("user.", StringComparison.Ordinal) ? _options.UserLogChannelId : _options.ServerLogChannelId;
            await _gateway.PostAsync(channel, StaffPost(entry, actor), cancellationToken).ConfigureAwait(false);

            if (entry.Action == BeatmapStatusChanger.LogAction && entry.Result is { } status && AnnouncedStatuses.Contains(status)
                && entry.TargetId is { } setId && community is not null
                && await community.GetBeatmapsetAsync((int)setId, cancellationToken).ConfigureAwait(false) is { } set)
            {
                await _gateway.PostAsync(_options.BeatmapsChannelId, BeatmapsetPost(set, status, actor), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A staff log entry as posted in the staff channels.</summary>
    public DiscordPost StaffPost(StaffLogEntry entry, User? actor)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var color = entry.Action switch
        {
            "user.restrict" or "user.evasion_blocked" or "user.ban" or "user.delete" => DiscordFormat.Danger,
            BanchoLoginHandler.SharedMachineAction or Anticheat.ClientFlags.RegistryAction => 0xF39C12u,
            "user.silence" => 0xFEE75Cu,
            "user.unrestrict" or "user.unsilence" or "user.verify" or "user.clear_standing" or "user.badge_add" => 0x57F287u,
            _ => DiscordFormat.Staff,
        };
        return new DiscordPost(Embed: new DiscordEmbed
        {
            AuthorName = actor?.Username ?? (entry.ActorId > 0 ? $"#{entry.ActorId.ToString(CultureInfo.InvariantCulture)}" : "Server"),
            AuthorUrl = actor is null ? null : _format.Profile(actor.Id),
            AuthorIconUrl = actor is null ? null : _format.Avatar(actor.Id),
            Description = DiscordFormat.Escape(entry.Summary),
            Color = color,
            Footer = entry.Result is { Length: > 0 } result ? $"{entry.Action} ▸ {result}" : entry.Action,
            Timestamp = new DateTimeOffset(DateTime.SpecifyKind(entry.CreatedAt, DateTimeKind.Utc)),
        });
    }

    /// <summary>A set's new status, for the beatmaps channel.</summary>
    public DiscordPost BeatmapsetPost(BeatmapsetCard set, string status, User? actor)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(status);
        var (emoji, color) = status.ToLowerInvariant() switch
        {
            "loved" => ("💖", 0xFF66AAu),
            "qualified" => ("✅", 0x66CCFFu),
            _ => ("⭐", 0xB3FF66u),
        };
        var name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(status.ToLowerInvariant());
        var versions = set.Versions.Count == 0 ? string.Empty
            : "\n" + string.Join(" ▸ ", set.Versions.Take(12).Select(DiscordFormat.Escape)) + (set.Versions.Count > 12 ? " …" : string.Empty);
        var by = actor is null ? string.Empty : $" by {DiscordFormat.Escape(actor.Username)}";
        return new DiscordPost(Embed: new DiscordEmbed
        {
            Title = $"{set.Artist} - {set.Title}",
            Url = _format.Beatmapset(set.BeatmapsetId),
            Description = $"{emoji} **{name}**{by}\nmapped by **{DiscordFormat.Escape(set.Creator)}**{versions}",
            ImageUrl = _format.Cover(set.BeatmapsetId),
            Color = color,
        });
    }

    private async Task OnLinkedAsync(int userId, ulong discordId)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId).ConfigureAwait(false);
            await _gateway.PostAsync(_options.UserLogChannelId, new DiscordPost(Embed: new DiscordEmbed
            {
                AuthorName = user?.Username ?? $"#{userId.ToString(CultureInfo.InvariantCulture)}",
                AuthorUrl = _format.Profile(userId),
                AuthorIconUrl = _format.Avatar(userId),
                Description = $"🔗 Linked to <@{discordId.ToString(CultureInfo.InvariantCulture)}>",
                Color = DiscordFormat.Staff,
                Footer = "discord.link",
            })).ConfigureAwait(false);
            await SyncRolesAsync(scope.ServiceProvider, userId, discordId, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // the periodic sync catches up; linking itself already succeeded
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Discord: couldn't finish linking {UserId}.", userId);
        }
    }

    private async Task OnUnlinkedAsync(ulong discordId)
    {
        try
        {
            await _gateway.SyncRolesAsync(discordId, _options.ManagedRoles(), new HashSet<ulong>()).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // roles of a member who left, or Discord being down: nothing to do
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Discord: couldn't take the roles of {DiscordId} back.", discordId);
        }
    }

    /// <summary>The roles a linked player should have: from their website groups, none while restricted.</summary>
    public async Task<IReadOnlySet<ulong>> WantedRolesAsync(IServiceProvider services, int userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var user = await services.GetRequiredService<IUserRepository>().GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsRestricted)
            return new HashSet<ulong>();

        var wanted = new HashSet<ulong>();
        if (_options.LinkedRoleId != 0)
            wanted.Add(_options.LinkedRoleId);
        if (services.GetService<OsuWebCommunity>() is { } community)
        {
            var roles = _options.GroupRoles();
            foreach (var group in await community.GetGroupsAsync(userId, cancellationToken).ConfigureAwait(false))
            {
                if (roles.TryGetValue(group, out var role) && role != 0)
                    wanted.Add(role);
            }
        }
        return wanted;
    }

    private async Task SyncRolesAsync(IServiceProvider services, int userId, ulong discordId, CancellationToken cancellationToken)
    {
        var managed = _options.ManagedRoles();
        if (managed.Count == 0)
            return;
        var wanted = await WantedRolesAsync(services, userId, cancellationToken).ConfigureAwait(false);
        await _gateway.SyncRolesAsync(discordId, managed, wanted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Medal descriptions may carry the website's HTML.</summary>
    private static string StripTags(string text) => System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", string.Empty);
}
