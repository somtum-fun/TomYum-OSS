using System.Globalization;
using TomYum.Persistence.OsuWeb;

namespace TomYum.Server.OsuWeb;

/// <summary>Settings for lazer's daily challenge (section <c>DailyChallenge</c>).</summary>
public sealed class DailyChallengeOptions
{
    public const string SectionName = "DailyChallenge";

    /// <summary>Keep the queue filled (false: staff fill <c>daily_challenge_queue</c> themselves).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many days are kept queued.</summary>
    public int DaysAhead { get; set; } = 7;

    public double MinStars { get; set; } = 4.0;

    public double MaxStars { get; set; } = 6.5;

    /// <summary>The rulesets, one per day in turn (e.g. <c>osu</c>, or <c>osu,osu,taiko,osu,osu,catch,mania</c>).</summary>
    public string Rulesets { get; set; } = "osu";

    /// <summary>The ruleset for <paramref name="day"/>: the list rotates by date, so a given day always gets the same one.</summary>
    public int RulesetFor(DateOnly day)
    {
        var list = Rulesets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => Discord.DiscordFormat.ParseRuleset(r) ?? (int.TryParse(r, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id is >= 0 and <= 3 ? id : (int?)null))
            .OfType<int>().ToList();
        return list.Count == 0 ? 0 : list[day.DayNumber % list.Count];
    }
}

/// <summary>
/// Keeps lazer's daily challenge queue filled with random ranked maps (never a set used before),
/// so osu-web's scheduler (<c>daily-challenge:create-next</c>, 00:05 UTC) always has a map to open
/// the day's room with. Players may add the usual mods but not rate changes, so everyone plays the
/// same song at the same speed.
/// </summary>
public sealed class DailyChallengeQueueJob : BackgroundService
{
    /// <summary>The mods players may add, per ruleset.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> AllowedMods =
    [
        ["EZ", "NF", "HD", "HR", "SD", "PF", "FL"],
        ["EZ", "NF", "HD", "HR", "SD", "PF", "FL"],
        ["EZ", "NF", "HD", "HR", "SD", "PF", "FL"],
        ["EZ", "NF", "HD", "FI", "SD", "PF", "FL", "MR"],
    ];

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly DailyChallengeOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<DailyChallengeQueueJob> _logger;

    public DailyChallengeQueueJob(DailyChallengeOptions options, IServiceScopeFactory scopes, TimeProvider time, ILogger<DailyChallengeQueueJob> logger)
    {
        _options = options;
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    /// <summary>Tops the queue up to <see cref="DailyChallengeOptions.DaysAhead"/>; returns how many were added.</summary>
    public async Task<int> FillAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var challenge = scope.ServiceProvider.GetRequiredService<OsuWebDailyChallenge>();
        var waiting = await challenge.WaitingAsync(cancellationToken).ConfigureAwait(false);
        var open = await challenge.CurrentAsync(cancellationToken).ConfigureAwait(false) is not null;

        // The first waiting entry opens today when nothing is open yet, else tomorrow.
        var firstDay = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime).AddDays(open ? 1 : 0);
        var added = 0;
        for (var index = waiting; index < _options.DaysAhead; index++)
        {
            var day = firstDay.AddDays(index);
            var ruleset = _options.RulesetFor(day);
            if (await challenge.PickMapAsync(ruleset, _options.MinStars, _options.MaxStars, cancellationToken).ConfigureAwait(false) is not { } beatmapId)
            {
                _logger.LogWarning("Daily challenge: no unused ranked {Ruleset} map between {Min} and {Max} stars left.",
                    Discord.DiscordFormat.Ruleset(ruleset), _options.MinStars, _options.MaxStars);
                break;
            }
            await challenge.EnqueueAsync(beatmapId, ruleset, AllowedMods[ruleset], [], null, cancellationToken).ConfigureAwait(false);
            added++;
        }
        if (added > 0)
            _logger.LogInformation("Daily challenge: queued {Added} map(s); {Waiting} waiting.", added, waiting + added);
        return added;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FillAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // a failed pass (database hiccup) must not stop the job
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "Daily challenge: filling the queue failed; trying again later.");
            }
            await Task.Delay(Interval, _time, stoppingToken).ConfigureAwait(false);
        }
    }
}
