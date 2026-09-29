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

    private async Task<DiscordReply> ScanAsync(IServiceProvider services, IDiscordLinkRepository links, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken)
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
        CircleguardScan scan;
        try
        {
            scan = await scanner.ScanAsync(services, community, score, path, ScanReferences, official, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return DiscordReply.Text("circleguard didn't answer; try again in a minute.", isPrivate: true);
        }

        var embed = CircleguardEmbed.Build(_format, score, scan, restricted: false, requestedBy: staff.Username);
        return new DiscordReply(new DiscordPost(Embed: embed) { Buttons = CircleguardEmbed.Buttons(_format, score, scan.Verdict.RiskScore, restricted: false) }, Private: true);
    }
}
