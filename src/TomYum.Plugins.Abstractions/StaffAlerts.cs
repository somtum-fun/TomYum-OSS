namespace TomYum.Plugins.Abstractions;

/// <summary>
/// Something staff should look at, from any plugin (an anti-cheat finding, say), shown wherever staff
/// are told things: the Discord bot posts it with moderation buttons. The plugin that raises it has
/// already done what it decided to (restricted the player, written the staff log).
/// </summary>
/// <param name="Source">Who found it, e.g. <c>circleguard</c>.</param>
/// <param name="UserId">The player it's about.</param>
/// <param name="Username">Their name.</param>
/// <param name="Headline">One line saying what was found, e.g. "Likely cheating ▸ risk 82/100".</param>
/// <param name="Details">The reasons, one per line (markdown links allowed).</param>
/// <param name="Severity">0 (fine) to 100 (certain): how loud to be about it.</param>
/// <param name="ScoreId">The website score it's about, if any.</param>
/// <param name="Restricted">Whether the plugin already restricted the player for it.</param>
public sealed record StaffAlert(
    string Source,
    int UserId,
    string Username,
    string Headline,
    string Details,
    int Severity,
    ulong? ScoreId = null,
    bool Restricted = false)
{
    /// <summary>Numbers and facts to show side by side.</summary>
    public IReadOnlyList<StaffAlertField> Fields { get; init; } = [];

    /// <summary>A short note under it (e.g. the mods checked, who asked for it).</summary>
    public string? Footer { get; init; }
}

/// <summary>One named value of a <see cref="StaffAlert"/>.</summary>
/// <param name="Name">The label.</param>
/// <param name="Value">The value (markdown links allowed).</param>
/// <param name="Inline">Whether it can sit beside others.</param>
public sealed record StaffAlertField(string Name, string Value, bool Inline = true);

/// <summary>A plugin raised a <see cref="StaffAlert"/>.</summary>
/// <param name="Alert">The alert.</param>
public sealed record StaffAlertEvent(StaffAlert Alert) : IPluginEvent;

/// <summary>
/// A plugin that can check a score on demand (an anti-cheat's "scan this play"), published with
/// <see cref="IPluginContext.PublishHostService{TService}"/> for other plugins (the Discord bot's /scan).
/// </summary>
public interface IScoreCheckService
{
    /// <summary>What does the checking, e.g. <c>circleguard</c>.</summary>
    string Name { get; }

    /// <summary>Checks a website score.</summary>
    /// <param name="scoreId">The website score id.</param>
    /// <param name="requestedBy">Who asked (shown with the result).</param>
    /// <param name="progress">Where to report each step, e.g. "comparing with osu!'s top plays (12/30)".</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The finding, or why there's none.</returns>
    Task<ScoreCheckResult> CheckAsync(ulong scoreId, string requestedBy, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IScoreCheckService.CheckAsync"/> found: an alert, or why it couldn't check.</summary>
/// <param name="Alert">The finding (whatever its severity), or null.</param>
/// <param name="Problem">Why there's no finding (no such score, no replay, the service down), or null.</param>
public sealed record ScoreCheckResult(StaffAlert? Alert, string? Problem);
