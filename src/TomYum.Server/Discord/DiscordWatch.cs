using System.Collections.Concurrent;
using System.Globalization;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.Discord;

/// <summary>
/// What <see cref="DiscordAnnouncer"/> watches beyond its own feeds:
/// <list type="bullet">
/// <item>reports (the website's, and <c>!report</c>) to the reports channel with Restrict/Silence/Dismiss buttons;</item>
/// <item>suspicious plays to the same channel: far above the player's best, passes that overlap in
/// time (faster than the map is long), and new accounts taking a #1 or a big play;</item>
/// <item>milestones in the public log: global rank tiers (top 1000 … #1), #1 in their country, and play counts.</item>
/// </list>
/// Milestones already reached before the bot saw a player aren't announced (their first sighting is
/// only recorded).
/// </summary>
public sealed class DiscordWatch
{
    /// <summary>Global rank tiers, best first.</summary>
    public static readonly int[] RankTiers = [1, 10, 50, 100, 500, 1000];

    /// <summary>Play count milestones.</summary>
    public static readonly long[] PlayTiers = [1_000, 5_000, 10_000, 25_000, 50_000, 100_000];

    /// <summary>How young an account is "new" for the suspicious-play check.</summary>
    public static readonly TimeSpan NewAccount = TimeSpan.FromDays(7);

    /// <summary>How long after a score its player's rank is read (ppy's processor updates it first).</summary>
    private static readonly TimeSpan MilestoneDelay = TimeSpan.FromSeconds(20);

    private readonly DiscordOptions _options;
    private readonly IDiscordGateway _gateway;
    private readonly DiscordFormat _format;
    private readonly DiscordState _state;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<int, (DateTime EndedAt, int BeatmapId)> _lastPass = new();
    private readonly ConcurrentDictionary<(int UserId, int Ruleset), DateTimeOffset> _milestoneQueue = new();
    private int _reportMark = -1;

    public DiscordWatch(DiscordOptions options, IDiscordGateway gateway, DiscordFormat format, DiscordState state, TimeProvider time)
    {
        _options = options;
        _gateway = gateway;
        _format = format;
        _state = state;
        _time = time;
    }

    /// <summary>Starts the report feed at the newest report.</summary>
    public async Task StartAsync(OsuWebCommunity community, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(community);
        _reportMark = await community.GetLatestReportIdAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One pass: new reports, and milestones of players who played a moment ago.</summary>
    public async Task TickAsync(OsuWebCommunity community, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(community);
        if (_reportMark >= 0)
        {
            foreach (var report in await community.GetReportsAfterAsync(_reportMark, cancellationToken).ConfigureAwait(false))
            {
                _reportMark = report.ReportId;
                await _gateway.PostAsync(_options.ReportsChannel, ReportPost(report), cancellationToken).ConfigureAwait(false);
            }
        }

        var now = _time.GetUtcNow();
        foreach (var (key, due) in _milestoneQueue)
        {
            if (due > now || !_milestoneQueue.TryRemove(key, out _))
                continue;
            if (await community.GetPlayerAsync(key.UserId, key.Ruleset, cancellationToken).ConfigureAwait(false) is { IsRestricted: false } player)
                await CheckMilestonesAsync(player, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A pass that was just announced-or-not: queues its milestones and checks it for suspicious signs.</summary>
    public async Task OnScoreAsync(OsuWebCommunity community, ScoreCard score, bool tookFirst, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(community);
        ArgumentNullException.ThrowIfNull(score);
        if (!score.Passed)
            return;
        _milestoneQueue[(score.UserId, score.Ruleset)] = _time.GetUtcNow() + MilestoneDelay;

        var reasons = new List<string>();
        if (score.Pp is { } pp)
        {
            var before = await community.GetBestPpBeforeAsync(score.UserId, score.Ruleset, score.ScoreId, cancellationToken).ConfigureAwait(false);
            if (before > 0 && pp >= 150 && pp > before * 1.5 && pp - before >= 100)
                reasons.Add($"**{DiscordFormat.Pp(pp)}**, far above their best so far ({DiscordFormat.Pp(before)})");
            else if (before <= 0 && pp >= 300)
                reasons.Add($"**{DiscordFormat.Pp(pp)}** on their first play with pp");
        }

        // Two passes can't overlap: the second one's map takes its length to play.
        var length = await community.GetMapLengthAsync(score.BeatmapId, cancellationToken).ConfigureAwait(false);
        var rate = score.Mods.Contains("DT") || score.Mods.Contains("NC") ? 1.5 : score.Mods.Contains("HT") || score.Mods.Contains("DC") ? 0.75 : 1.0;
        var needed = length / rate;
        if (_lastPass.TryGetValue(score.UserId, out var previous) && previous.BeatmapId != score.BeatmapId && needed >= 30)
        {
            var gap = (score.EndedAt - previous.EndedAt).TotalSeconds;
            if (gap >= 0 && gap < needed * 0.8 - 5)
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"finished a {needed:0}s map only {gap:0}s after their previous pass"));
        }
        _lastPass[score.UserId] = (score.EndedAt, score.BeatmapId);

        if (tookFirst || score.Pp >= _options.BigPlayPp)
        {
            if (await community.GetPlayerAsync(score.UserId, score.Ruleset, cancellationToken).ConfigureAwait(false) is { } player
                && _time.GetUtcNow().UtcDateTime - player.JoinedAt < NewAccount)
                reasons.Add($"a {(tookFirst ? "#1" : "big play")} from an account made {DiscordFormat.Relative(player.JoinedAt)}");
        }

        if (reasons.Count > 0)
            await _gateway.PostAsync(_options.ReportsChannel, SuspiciousPost(score, reasons), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A report, with staff buttons.</summary>
    public DiscordPost ReportPost(ReportCard report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var what = report.Type switch
        {
            null or "user" => "the player",
            "solo_score" or "score_best_osu" or "score_best_taiko" or "score_best_fruits" or "score_best_mania" when report.ReportableId is { } score =>
                $"[a score]({_format.Site}/scores/{score.ToString(CultureInfo.InvariantCulture)})",
            "message" => "a chat message",
            "comment" => "a comment",
            "forum_post" => "a forum post",
            "beatmapset" when report.ReportableId is { } set => $"[a beatmap set]({_format.Beatmapset((int)set)})",
            "beatmapset_discussion_post" => "a modding post",
            var other => other,
        };
        var reason = report.Reason ?? "Other";
        return new DiscordPost(Embed: new DiscordEmbed
        {
            AuthorName = $"Report #{report.ReportId.ToString(CultureInfo.InvariantCulture)}: {report.Username}",
            AuthorUrl = _format.Profile(report.UserId),
            AuthorIconUrl = _format.Avatar(report.UserId),
            Description = $"**{Spaced(reason)}**, about {what}\n{(report.Comments.Length > 0 ? ">>> " + DiscordFormat.Escape(Shorten(report.Comments, 1500)) : "*No comments.*")}",
            Fields = [new DiscordField("Reported by", $"[{DiscordFormat.LinkLabel(report.ReporterName)}]({_format.Profile(report.ReporterId)})")],
            Color = 0xFEE75C,
            Timestamp = new DateTimeOffset(report.At, TimeSpan.Zero),
        })
        {
            Buttons = ModerationButtons(report.UserId, $"Reported for {Spaced(reason)} (report {report.ReportId.ToString(CultureInfo.InvariantCulture)})", report.UserId),
        };
    }

    /// <summary>A suspicious play, with staff buttons.</summary>
    public DiscordPost SuspiciousPost(ScoreCard score, IReadOnlyList<string> reasons)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(reasons);
        return new DiscordPost(Embed: new DiscordEmbed
        {
            AuthorName = $"Suspicious play: {score.Username}",
            AuthorUrl = _format.Profile(score.UserId),
            AuthorIconUrl = _format.Avatar(score.UserId),
            Title = DiscordFormat.ScoreTitle(score),
            Url = $"{_format.Site}/scores/{score.ScoreId.ToString(CultureInfo.InvariantCulture)}",
            Description = string.Join('\n', reasons.Select(r => "⚠️ " + r)) + "\n" + DiscordFormat.ScoreDetails(score),
            Color = DiscordFormat.Danger,
            Footer = $"{DiscordFormat.Ruleset(score.Ruleset)} ▸ {(score.IsStable ? "stable" : "lazer")}",
            Timestamp = new DateTimeOffset(score.EndedAt, TimeSpan.Zero),
        })
        {
            Buttons = ModerationButtons(score.UserId, $"Suspicious play (score {score.ScoreId.ToString(CultureInfo.InvariantCulture)})", score.UserId),
        };
    }

    /// <summary>A play circleguard flagged (and whether it restricted the player), to the reports channel.</summary>
    public Task CircleguardAsync(ScoreCard score, Anticheat.CircleguardVerdict verdict, bool restricted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(verdict);
        var numbers = new List<string>();
        if (verdict.ConvertedUnstableRate is { } ur)
            numbers.Add(string.Create(CultureInfo.InvariantCulture, $"UR {ur:0.00}"));
        if (verdict.Frametime is { } frametime)
            numbers.Add(string.Create(CultureInfo.InvariantCulture, $"frametime {frametime:0.0}ms"));
        numbers.Add(string.Create(CultureInfo.InvariantCulture, $"{verdict.SnapCount} snap(s)"));
        if (verdict.Similarity is { } similarity && similarity.Comparisons.Count > 0)
            numbers.Add(string.Create(CultureInfo.InvariantCulture, $"closest top play {similarity.Comparisons.Min(c => c.Distance):0.0}px ({similarity.Comparisons.Count} compared)"));

        var headline = restricted ? $"⛔ **Restricted automatically** (risk {verdict.RiskScore}/100)" : $"⚠️ Risk **{verdict.RiskScore}/100**";
        var reasons = verdict.Reasons.Count == 0 ? string.Empty : "\n" + string.Join('\n', verdict.Reasons.Select(r => "• " + DiscordFormat.Escape(r)));
        var user = score.UserId.ToString(CultureInfo.InvariantCulture);
        var why = Shorten($"circleguard risk {verdict.RiskScore} (score {score.ScoreId.ToString(CultureInfo.InvariantCulture)})", 60);
        IReadOnlyList<DiscordButton> buttons = restricted
            ?
            [
                new DiscordButton("Unrestrict (false alarm)", DiscordButtonStyle.Success, $"mod:unrestrict:{user}:circleguard false alarm"),
                new DiscordButton("Dismiss", DiscordButtonStyle.Secondary, $"mod:dismiss:{user}:-"),
                new DiscordButton("Profile", DiscordButtonStyle.Link, Url: _format.Profile(score.UserId)),
            ]
            : ModerationButtons(score.UserId, why, score.UserId);

        return _gateway.PostAsync(_options.ReportsChannel, new DiscordPost(Embed: new DiscordEmbed
        {
            AuthorName = $"circleguard: {score.Username}",
            AuthorUrl = _format.Profile(score.UserId),
            AuthorIconUrl = _format.Avatar(score.UserId),
            Title = DiscordFormat.ScoreTitle(score),
            Url = $"{_format.Site}/scores/{score.ScoreId.ToString(CultureInfo.InvariantCulture)}",
            Description = headline + reasons + "\n" + DiscordFormat.ScoreDetails(score),
            Fields = [new DiscordField("Numbers", string.Join(" ▸ ", numbers), Inline: false)],
            Color = restricted ? DiscordFormat.Danger : 0xF39C12,
            Footer = $"{DiscordFormat.Ruleset(score.Ruleset)} ▸ {(score.IsStable ? "stable" : "lazer")} ▸ {verdict.Mods}" + (verdict.Note is { } note ? " ▸ " + note : string.Empty),
            Timestamp = new DateTimeOffset(score.EndedAt, TimeSpan.Zero),
        })
        { Buttons = buttons }, cancellationToken);
    }

    private IReadOnlyList<DiscordButton> ModerationButtons(int userId, string reason, int profileId)
    {
        // Button ids are split on ':' and capped at 100 characters.
        var safe = Shorten(reason.Replace(':', ' '), 60);
        var user = userId.ToString(CultureInfo.InvariantCulture);
        return
        [
            new DiscordButton("Restrict", DiscordButtonStyle.Danger, $"mod:restrict:{user}:{safe}"),
            new DiscordButton("Silence 1 day", DiscordButtonStyle.Primary, $"mod:silence:{user}:{safe}"),
            new DiscordButton("Dismiss", DiscordButtonStyle.Secondary, $"mod:dismiss:{user}:-"),
            new DiscordButton("Profile", DiscordButtonStyle.Link, Url: _format.Profile(profileId)),
        ];
    }

    private async Task CheckMilestonesAsync(PlayerCard player, CancellationToken cancellationToken)
    {
        var field = string.Create(CultureInfo.InvariantCulture, $"{player.UserId}:{player.Ruleset}");
        var tier = player.GlobalRank is { } rank ? RankTiers.Where(t => rank <= t).DefaultIfEmpty(int.MaxValue).Min() : int.MaxValue;
        var countryFirst = player.CountryRank == 1 && player.GlobalRank is not null;
        var plays = PlayTiers.Where(t => player.PlayCount >= t).DefaultIfEmpty(0).Max();
        var current = string.Create(CultureInfo.InvariantCulture, $"{tier}|{(countryFirst ? 1 : 0)}|{plays}");

        var stored = await _state.GetFieldAsync("milestones", field).ConfigureAwait(false);
        if (stored is null)
        {
            // First sighting: remember where they are without announcing it.
            await _state.SetFieldAsync("milestones", field, current).ConfigureAwait(false);
            return;
        }
        var parts = stored.Split('|');
        var bestTier = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var wasCountryFirst = parts[1] == "1";
        var bestPlays = long.Parse(parts[2], CultureInfo.InvariantCulture);

        var lines = new List<string>();
        var name = DiscordFormat.Escape(player.Username);
        var ruleset = DiscordFormat.Ruleset(player.Ruleset);
        if (tier < bestTier)
        {
            lines.Add(tier == 1
                ? $"👑 **{name}** is now **#1** in {ruleset}!"
                : string.Create(CultureInfo.InvariantCulture, $"📈 **{name}** reached the **top {DiscordFormat.Number(tier)}** in {ruleset} (#{DiscordFormat.Number(player.GlobalRank!.Value)})!"));
        }
        if (countryFirst && !wasCountryFirst && tier != 1)
            lines.Add($"{DiscordFormat.Flag(player.Country)} **{name}** is now **#1 in their country** in {ruleset}!");
        if (plays > bestPlays)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"🎮 **{name}** played their **{DiscordFormat.Number(plays)}th** {ruleset} map!"));

        var kept = string.Create(CultureInfo.InvariantCulture, $"{Math.Min(tier, bestTier)}|{(countryFirst || wasCountryFirst ? 1 : 0)}|{Math.Max(plays, bestPlays)}");
        if (kept != stored)
            await _state.SetFieldAsync("milestones", field, kept).ConfigureAwait(false);
        if (lines.Count == 0)
            return;

        await _gateway.PostAsync(_options.PublicLogChannelId, new DiscordPost(Embed: new DiscordEmbed
        {
            AuthorName = player.Username,
            AuthorUrl = _format.ProfileIn(player.UserId, player.Ruleset),
            AuthorIconUrl = _format.Avatar(player.UserId),
            Description = string.Join('\n', lines),
            Color = DiscordFormat.Gold,
        }), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>"MultipleAccounts" → "Multiple accounts".</summary>
    private static string Spaced(string reason)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(reason, "(?<=[a-z])(?=[A-Z])", " ");
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
