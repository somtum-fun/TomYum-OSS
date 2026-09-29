using System.Globalization;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Anticheat;

namespace TomYum.Server.Discord;

/// <summary><c>/scan</c>: staff check any score with circleguard, compared with the map's top 30.</summary>
public sealed partial class DiscordCommands
{
    /// <summary>How many of the map's top plays here a scan compares with.</summary>
    public const int ScanReferences = 30;

    private async Task<DiscordReply> ScanAsync(
        IServiceProvider services, IDiscordLinkRepository links, IReadOnlyDictionary<string, string> options, ulong callerId, Action<DiscordPost>? progress, CancellationToken cancellationToken)
    {
        var (staff, error) = await StaffCallerAsync(services, links, callerId, cancellationToken).ConfigureAwait(false);
        if (staff is null)
            return DiscordReply.Text(error!, isPrivate: true);
        if (services.GetService<CircleguardScanner>() is not { } scanner || services.GetService<OsuWebCommunity>() is not { } community)
            return DiscordReply.Text("circleguard isn't set up on this server.", isPrivate: true);

        var text = options.GetValueOrDefault("score")?.Trim() ?? string.Empty;
        ulong scoreId;
        if (ScoreLink().Match(text) is { Success: true } link)
            scoreId = ulong.Parse(link.Groups[1].Value, CultureInfo.InvariantCulture);
        else if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out scoreId))
            return DiscordReply.Text("Give the score's link from the website (…/scores/123) or its id.", isPrivate: true);

        if (await community.GetScoreCardAsync(scoreId, cancellationToken).ConfigureAwait(false) is not { } score)
            return DiscordReply.Text("There's no such score on the website.", isPrivate: true);
        if (await scanner.ReplayPathAsync(community, score, cancellationToken).ConfigureAwait(false) is not { } path || !File.Exists(path))
            return DiscordReply.Text("That score has no replay here (failed plays and some older ones don't), so it can't be scanned.", isPrivate: true);

        var official = int.TryParse(options.GetValueOrDefault("official"), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : ScanReferences;
        var comparesOfficial = official > 0 && scanner.CanCompareOfficial && score.Ruleset == 0 && score.BeatmapId < TomYum.Domain.Entities.Beatmap.CustomIdFloor;
        var live = new ScanStatus(_format, score, staff.Username, comparesOfficial, DateTimeOffset.UtcNow);
        progress?.Invoke(live.Render());
        CircleguardScan scan;
        try
        {
            scan = await scanner.ScanAsync(services, community, score, path, ScanReferences, official, step =>
            {
                live.Update(step);
                progress?.Invoke(live.Render());
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return DiscordReply.Text("circleguard didn't answer; try again in a minute.", isPrivate: true);
        }

        var embed = CircleguardEmbed.Build(_format, score, scan, restricted: false, requestedBy: staff.Username);
        return new DiscordReply(new DiscordPost(Embed: embed) { Buttons = CircleguardEmbed.Buttons(_format, score, scan.Verdict.RiskScore, restricted: false) });
    }

    /// <summary>The live "scanning…" card: each step done, going or waiting, with counts.</summary>
    private sealed class ScanStatus(DiscordFormat format, ScoreCard score, string requestedBy, bool official, DateTimeOffset started)
    {
        private CircleguardScanProgress _at = new(CircleguardScanStep.TopPlays, 0, 0);
        private (int Done, int Total) _top;
        private (int Done, int Total) _osu;

        public void Update(CircleguardScanProgress step)
        {
            _at = step;
            if (step.Step == CircleguardScanStep.TopPlays)
                _top = (step.Done, step.Total);
            else if (step.Step == CircleguardScanStep.OfficialPlays)
                _osu = (step.Done, step.Total);
        }

        public DiscordPost Render()
        {
            string Mark(CircleguardScanStep step) => _at.Step == step ? "⏳" : _at.Step > step ? "✅" : "⬜";
            static string Count((int Done, int Total) at) => string.Create(CultureInfo.InvariantCulture, $" ▸ {at.Done}/{at.Total}");

            var lines = new List<string>
            {
                "✅ Replay found",
                $"{Mark(CircleguardScanStep.TopPlays)} Top plays here{(_top.Total > 0 ? Count(_top) : string.Empty)}",
                official
                    ? $"{Mark(CircleguardScanStep.OfficialPlays)} osu!'s top plays{(_osu.Total > 0 ? Count(_osu) : string.Empty)}"
                      + (_at.Step == CircleguardScanStep.OfficialPlays ? " (osu! allows 10 a minute; they're kept for next time)" : string.Empty)
                    : "➖ osu!'s top plays (not compared)",
                $"{Mark(CircleguardScanStep.Analysing)} circleguard's analysis",
            };
            return new DiscordPost(Embed: new DiscordEmbed
            {
                AuthorName = $"circleguard ▸ {score.Username}",
                AuthorUrl = format.Profile(score.UserId),
                AuthorIconUrl = format.Avatar(score.UserId),
                Title = DiscordFormat.ScoreTitle(score),
                Url = $"{format.Site}/scores/{score.ScoreId.ToString(CultureInfo.InvariantCulture)}",
                ThumbnailUrl = format.ListCover(score.BeatmapsetId),
                Description = string.Create(CultureInfo.InvariantCulture, $"🔍 **Scanning…** (started <t:{started.ToUnixTimeSeconds()}:R>)\n\n") + string.Join('\n', lines),
                Color = DiscordFormat.Staff,
                Footer = $"circleguard ▸ scanned by {requestedBy}",
            });
        }
    }
}
