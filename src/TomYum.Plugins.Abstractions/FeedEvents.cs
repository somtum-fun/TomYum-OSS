namespace TomYum.Plugins.Abstractions;

// Feed events: raised after the data is stored, from the website's tables (so stable, lazer and the website's own
// actions alike), by TomYum's website feed, a few seconds behind. Handlers run off the request path; each event is
// raised once per server, in order, starting from what's new after the server started.

/// <summary>A staff log entry was written (by TomYum or the website).</summary>
/// <param name="Id">The entry's id.</param>
/// <param name="Action">What was done, dotted lowercase (<c>beatmapset.import</c>, <c>user.restrict</c>, ...).</param>
/// <param name="ActorId">Who did it (0: the system).</param>
/// <param name="TargetId">What it was done to (a user, set or score id, by action), or null.</param>
/// <param name="Summary">The entry's text.</param>
/// <param name="Result">Its result, when the action has one (e.g. a set's new status), or null.</param>
/// <param name="CreatedAt">When, UTC.</param>
public sealed record StaffLogEvent(long Id, string Action, long ActorId, long? TargetId, string Summary, string? Result, DateTimeOffset CreatedAt) : IPluginEvent;

/// <summary>
/// A score was stored and processed (pp worked out): stable plays once mirrored to the website, lazer plays once
/// ppy's score processor is done with them (or after two minutes without it).
/// </summary>
/// <param name="WebsiteScoreId">The website's score id (<c>scores.id</c>).</param>
/// <param name="UserId">The player.</param>
/// <param name="BeatmapId">The map.</param>
/// <param name="RulesetId">The ruleset it was played in (0-3, or a custom ruleset's id).</param>
/// <param name="Pp">Its pp, or null (unranked, failed, no pp for its mods).</param>
/// <param name="Rank">Its grade (<c>XH</c>, <c>X</c>, <c>SH</c>, <c>S</c>, <c>A</c>, ... <c>F</c>).</param>
/// <param name="Accuracy">Accuracy, 0-1.</param>
/// <param name="TotalScore">Its score.</param>
/// <param name="MaxCombo">Its max combo.</param>
/// <param name="Passed">Whether it passed.</param>
/// <param name="Ranked">Whether it counts on leaderboards (a ranked map and mods).</param>
/// <param name="IsLazer">Played on lazer (TumThai) rather than osu!(stable).</param>
/// <param name="EndedAt">When the play ended, UTC.</param>
public sealed record ScoreProcessedEvent(
    long WebsiteScoreId,
    long UserId,
    long BeatmapId,
    int RulesetId,
    double? Pp,
    string Rank,
    double Accuracy,
    long TotalScore,
    int MaxCombo,
    bool Passed,
    bool Ranked,
    bool IsLazer,
    DateTimeOffset EndedAt) : IPluginEvent;

/// <summary>A player earned a medal (osu!'s or a custom one).</summary>
/// <param name="UserId">The player.</param>
/// <param name="AchievementId">The medal's id (custom medals from 100000).</param>
/// <param name="Name">The medal's name.</param>
/// <param name="AwardedAt">When, UTC.</param>
public sealed record MedalAwardedEvent(long UserId, long AchievementId, string Name, DateTimeOffset AwardedAt) : IPluginEvent;

/// <summary>A new account was made (in the game or on the website).</summary>
/// <param name="UserId">The account.</param>
/// <param name="Username">Its name.</param>
/// <param name="Country">Two-letter country code, lower case.</param>
/// <param name="RegisteredAt">When, UTC.</param>
public sealed record UserRegisteredEvent(long UserId, string Username, string Country, DateTimeOffset RegisteredAt) : IPluginEvent;

/// <summary>
/// A beatmap set's status changed: by staff (the panel, <c>!map</c>) or automatically (a qualified set becoming
/// ranked). A set that becomes ranked or approved starts with an empty leaderboard.
/// </summary>
/// <param name="BeatmapSetId">The set.</param>
/// <param name="Status">The new status by name (<c>ranked</c>, <c>approved</c>, <c>qualified</c>, <c>loved</c>, <c>pending</c>, ...).</param>
/// <param name="ActorId">Who changed it (0: automatic).</param>
/// <param name="Automatic">Whether nobody did it by hand.</param>
/// <param name="ChangedAt">When, UTC.</param>
public sealed record BeatmapStatusChangedEvent(long BeatmapSetId, string Status, long ActorId, bool Automatic, DateTimeOffset ChangedAt) : IPluginEvent;

/// <summary>A player or a staff tool reported a player (the website's reports).</summary>
/// <param name="ReportId">The report.</param>
/// <param name="ReporterId">Who reported (0: the system).</param>
/// <param name="TargetUserId">Who was reported.</param>
/// <param name="Reason">The report's reason (<c>Cheating</c>, <c>Insults</c>, ...).</param>
/// <param name="Comments">What the reporter wrote.</param>
/// <param name="ReportedAt">When, UTC.</param>
public sealed record ReportFiledEvent(long ReportId, long ReporterId, long TargetUserId, string Reason, string Comments, DateTimeOffset ReportedAt) : IPluginEvent;

/// <summary>
/// A chat message was said anywhere (osu!(stable), IRC, the website, lazer, or a relay through
/// <see cref="IHostChat"/>), in a channel or in private. Raised as it's recorded; keep handlers cheap.
/// </summary>
/// <param name="Channel">The channel (<c>#osu</c>), or the recipient's name for a private message.</param>
/// <param name="SenderId">Who said it.</param>
/// <param name="Sender">Their name.</param>
/// <param name="Text">What they said.</param>
/// <param name="RecipientId">The recipient of a private message, or null for a channel.</param>
/// <param name="Origin">Where it was said: <c>game</c>, <c>website</c>, <c>discord</c>, or a relay's tag given to <see cref="IHostChat.PostAsync"/>.</param>
public sealed record ChatRecordedEvent(string Channel, long SenderId, string Sender, string Text, long? RecipientId, string Origin) : IPluginEvent;

/// <summary>
/// The server logged an error (rate-limited: the same category at most once a minute, and the total too). For a
/// server-errors channel; a handler that logs an error itself doesn't raise another.
/// </summary>
/// <param name="Category">Where it was logged (the logger's category, e.g. <c>TomYum.Server.Beatmaps.ScoreSubmissionEndpoint</c>).</param>
/// <param name="Message">The log message.</param>
/// <param name="Exception">The exception's type and message, or null.</param>
/// <param name="Suppressed">How many errors of this category were left out since the last one raised.</param>
/// <param name="LoggedAt">When, UTC.</param>
public sealed record ServerErrorEvent(string Category, string Message, string? Exception, int Suppressed, DateTimeOffset LoggedAt) : IPluginEvent;
