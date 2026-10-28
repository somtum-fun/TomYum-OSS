using System.Globalization;
using TomYum.Bancho;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;

namespace TomYum.Server.OsuWeb;

/// <summary>
/// What osu! does when a play lands on a map's leaderboard, for stable and lazer plays alike (it
/// follows the website's scores):
/// <list type="bullet">
/// <item>a new best in the top <see cref="MaxPosition"/>: the "achieved rank #N on …" entry in the
/// player's recent activity (osu-web writes it, through its InterOp endpoint);</item>
/// <item>a new #1: the previous holder's "has lost first place on …" entry, and BanchoBot says so in
/// <c>#announce</c> (in-game, and in website/lazer chat).</item>
/// </list>
/// Plays from before this existed get their entries once, on the first start (dated that day; no
/// <c>#announce</c> for old plays), marked <see cref="BackfillName"/> in <c>osu_counts</c>.
/// </summary>
public sealed class ScoreRankEvents : BackgroundService
{
    /// <summary>Positions up to this get an activity entry (osu!'s rule).</summary>
    public const int MaxPosition = 1000;

    /// <summary>The <c>osu_counts</c> row marking the one-off backfill done.</summary>
    public const string BackfillName = "tomyum_rank_events_backfill";

    /// <summary>
    /// The <c>osu_counts</c> row marking done the one-off catch-up of Relax/Autopilot plays' entries (for a while
    /// the website's placement left them out, so they got none).
    /// </summary>
    public const string RelaxCatchUpName = "tomyum_rank_events_relax_catchup";

    /// <summary>How long a new Relax/Autopilot play is waited for (TomYum takes it in after the website has it).</summary>
    private static readonly TimeSpan ImportWait = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private readonly IServiceScopeFactory _scopes;
    private readonly ChannelRegistry _channels;
    private readonly string _site;
    private readonly ILogger<ScoreRankEvents> _logger;
    private readonly OsuWebChatBridge? _bridge;
    private ulong? _mark;

    public ScoreRankEvents(
        IServiceScopeFactory scopes, ChannelRegistry channels, IConfiguration configuration,
        ILogger<ScoreRankEvents> logger, OsuWebChatBridge? bridge = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _scopes = scopes;
        _channels = channels;
        _logger = logger;
        _bridge = bridge;
        var domain = configuration["Server:Domain"];
        _site = string.IsNullOrWhiteSpace(domain) ? "https://osu.ppy.sh" : $"https://osu.{domain}";
    }

    /// <summary>One pass over the scores set since the last one (the first pass only notes where to start).</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var community = scope.ServiceProvider.GetRequiredService<OsuWebCommunity>();
        var interop = scope.ServiceProvider.GetRequiredService<OsuWebInterop>();
        if (_mark is not { } mark)
        {
            _mark = await community.GetLatestScoreIdAsync(cancellationToken).ConfigureAwait(false);
            if (!await community.WasDoneAsync(BackfillName, cancellationToken).ConfigureAwait(false))
                await BackfillAsync(community, interop, _mark.Value, cancellationToken).ConfigureAwait(false);
            if (!await community.WasDoneAsync(RelaxCatchUpName, cancellationToken).ConfigureAwait(false))
                await RelaxCatchUpAsync(scope.ServiceProvider, community, interop, _mark.Value, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var score in await community.GetScoresAfterAsync(mark, 200, cancellationToken).ConfigureAwait(false))
        {
            // A custom lazer ruleset's play (10 and up) is on the website's own score boards for it, not announced here.
            if (score.Ruleset is < 0 or > 3)
            {
                _mark = score.ScoreId;
                continue;
            }

            // A Relax/Autopilot play is placed on TomYum's board, once TomYum has taken it in (waited for, briefly).
            var result = await ModePlacements.PlacementAsync(scope.ServiceProvider, community, score, cancellationToken).ConfigureAwait(false);
            if (result.Pending && DateTime.UtcNow - score.EndedAt < ImportWait)
                break;
            if (result.Placement is { Position: <= MaxPosition } placement)
                await AnnounceAsync(interop, score, placement, cancellationToken).ConfigureAwait(false);
            _mark = score.ScoreId;
        }
    }

    /// <summary>
    /// Relax/Autopilot plays of the last two days (up to <paramref name="upTo"/>) that placed but have no entry: for a
    /// while the website's placement (vanilla plays only) left them out. Dated now, as the backfill's; no #announce.
    /// </summary>
    private async Task RelaxCatchUpAsync(IServiceProvider services, OsuWebCommunity community, OsuWebInterop interop, ulong upTo, CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddDays(-2);
        var written = 0;
        ulong after = 0;
        while (true)
        {
            var page = await community.GetScoresAfterAsync(after, 200, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
                break;
            foreach (var score in page)
            {
                after = score.ScoreId;
                if (score.ScoreId > upTo || score.EndedAt < since || !OsuWebCommunity.IsRelaxOrAutopilot(score))
                    continue;
                if ((await ModePlacements.PlacementAsync(services, community, score, cancellationToken).ConfigureAwait(false)).Placement is not { Position: <= MaxPosition } placement
                    || await community.HasRankEventSinceAsync(score.UserId, score.BeatmapId, score.EndedAt.AddMinutes(-1), cancellationToken).ConfigureAwait(false))
                    continue;
                await interop.RankAchievedAsync(score.UserId, score.BeatmapId, score.Ruleset, placement.Position, score.Grade, score.IsStable, cancellationToken).ConfigureAwait(false);
                written++;
            }
            if (after >= upTo)
                break;
        }
        await community.MarkDoneAsync(RelaxCatchUpName, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Rank events: {Count} Relax/Autopilot entry(ies) caught up.", written);
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
#pragma warning disable CA1031 // a failed pass (website down) is tried again; the scores wait
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "Rank events: a pass failed; trying again shortly.");
            }
            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The entries for plays set before these entries existed: up to <paramref name="upTo"/>, or the
    /// first entry written live, whichever is earlier.
    /// </summary>
    private async Task BackfillAsync(OsuWebCommunity community, OsuWebInterop interop, ulong upTo, CancellationToken cancellationToken)
    {
        var liveSince = await community.FirstRankEventAtAsync(cancellationToken).ConfigureAwait(false);
        var written = 0;
        ulong after = 0;
        while (true)
        {
            var page = await community.GetScoresAfterAsync(after, 200, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
                break;
            foreach (var score in page)
            {
                after = score.ScoreId;
                if (score.ScoreId > upTo || (liveSince is { } since && score.EndedAt >= since) || score.Ruleset is < 0 or > 3)
                    continue;
                if (await community.GetPlacementAsync(score, cancellationToken).ConfigureAwait(false) is not { Position: <= MaxPosition } placement)
                    continue;
                await interop.RankAchievedAsync(score.UserId, score.BeatmapId, score.Ruleset, placement.Position, score.Grade, score.IsStable, cancellationToken).ConfigureAwait(false);
                if (placement.Position == 1 && placement.PreviousLeaderId is { } previous && previous != score.UserId)
                    await interop.FirstPlaceLostAsync(previous, score.BeatmapId, score.Ruleset, score.IsStable, cancellationToken).ConfigureAwait(false);
                written++;
            }
            if (after >= upTo)
                break;
        }
        await community.MarkDoneAsync(BackfillName, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Rank events: {Count} entry(ies) written for earlier plays.", written);
    }

    private async Task AnnounceAsync(OsuWebInterop interop, ScoreCard score, ScorePlacement placement, CancellationToken cancellationToken)
    {
        await interop.RankAchievedAsync(score.UserId, score.BeatmapId, score.Ruleset, placement.Position, score.Grade, score.IsStable, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("{Player} achieved rank #{Position} on beatmap {BeatmapId} ({Ruleset}).", score.Username, placement.Position, score.BeatmapId, score.Ruleset);
        if (placement.Position != 1)
            return;

        if (placement.PreviousLeaderId is { } previous && previous != score.UserId)
            await interop.FirstPlaceLostAsync(previous, score.BeatmapId, score.Ruleset, score.IsStable, cancellationToken).ConfigureAwait(false);
        Announce(score);
    }

    /// <summary>"Player achieved rank #1 on Map (ruleset)" in #announce: with links in-game, plain on the website.</summary>
    private void Announce(ScoreCard score)
    {
        var ruleset = Rulesets.Name(score.Ruleset);
        var map = $"{score.Artist} - {score.Title} [{score.Version}]";
        var user = string.Create(CultureInfo.InvariantCulture, $"{_site}/u/{score.UserId}");
        var beatmap = string.Create(CultureInfo.InvariantCulture, $"{_site}/b/{score.BeatmapId}");
        var inGame = $"[{user} {score.Username}] achieved rank #1 on [{beatmap} {map}] ({ruleset})";

        if (_channels.Get("#announce") is { } channel)
        {
            var writer = new BanchoPacketWriter(256);
            ChatEncoder.WriteMessage(writer, new BanchoMessage(BanchoBot.Name, inGame, channel.Name, BanchoBot.UserId));
            var bytes = writer.ToArray();
            foreach (var member in channel.Members)
                member.Enqueue(bytes);
        }
        _bridge?.ForwardChannelMessage(BanchoBot.UserId, "#announce", $"{score.Username} achieved rank #1 on {map} ({ruleset}) {beatmap}");
    }
}
