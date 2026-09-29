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
/// </summary>
public sealed class ScoreRankEvents : BackgroundService
{
    /// <summary>Positions up to this get an activity entry (osu!'s rule).</summary>
    public const int MaxPosition = 1000;

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
            return;
        }

        foreach (var score in await community.GetScoresAfterAsync(mark, 200, cancellationToken).ConfigureAwait(false))
        {
            if (await community.GetPlacementAsync(score, cancellationToken).ConfigureAwait(false) is { Position: <= MaxPosition } placement)
                await AnnounceAsync(interop, score, placement, cancellationToken).ConfigureAwait(false);
            _mark = score.ScoreId;
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
#pragma warning disable CA1031 // a failed pass (website down) is tried again; the scores wait
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "Rank events: a pass failed; trying again shortly.");
            }
            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
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
        var ruleset = Discord.DiscordFormat.Ruleset(score.Ruleset);
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
