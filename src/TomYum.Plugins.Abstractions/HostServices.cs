namespace TomYum.Plugins.Abstractions;

/// <summary>
/// Runs BanchoBot's own commands (<c>!restrict</c>, <c>!map rank</c>, a plugin's <c>!dan</c>, ...) for a plugin, with
/// the same permission checks, staff log and in-game effect as typing them in chat. Resolve it with
/// <see cref="IPluginContext.GetHostService{TService}"/> (trusted plugins only).
/// </summary>
public interface IHostCommands
{
    /// <summary>
    /// Runs <paramref name="commandLine"/> as user <paramref name="asUserId"/>, who needs the permissions the command
    /// asks for (an offline user too). Never a way around them: a Discord member runs as their linked account.
    /// </summary>
    /// <param name="asUserId">The account the command runs as.</param>
    /// <param name="commandLine">The command, with its <c>!</c>, e.g. <c>!silence peppy 10m spam</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>BanchoBot's reply, or null when the account doesn't exist or the line isn't a command.</returns>
    Task<string?> RunAsAsync(long asUserId, string commandLine, CancellationToken cancellationToken = default);

    /// <summary>Runs <paramref name="commandLine"/> as BanchoBot itself, for a plugin's own system actions.</summary>
    /// <param name="commandLine">The command, with its <c>!</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>BanchoBot's reply, or null when the line isn't a command.</returns>
    Task<string?> RunAsBotAsync(string commandLine, CancellationToken cancellationToken = default);
}

/// <summary>An account as plugins see it (<see cref="IHostAccounts"/>).</summary>
/// <param name="UserId">The account's id.</param>
/// <param name="Username">Its name.</param>
/// <param name="Country">Two-letter country code, lower case (<c>xx</c> when unknown).</param>
/// <param name="Groups">The website's group identifiers (<c>admin</c>, <c>gmt</c>, <c>nat</c>, <c>bng</c>, ...).</param>
/// <param name="Restricted">Whether the account is restricted.</param>
/// <param name="SilencedUntil">When a silence ends, or null.</param>
/// <param name="SupporterUntil">When supporter ends, or null when the account isn't a supporter.</param>
/// <param name="Whitelisted">Whether staff put the account on the anti-cheat whitelist.</param>
/// <param name="IsBot">Whether it's a bot account.</param>
public sealed record HostAccount(
    long UserId,
    string Username,
    string Country,
    IReadOnlySet<string> Groups,
    bool Restricted,
    DateTimeOffset? SilencedUntil,
    DateTimeOffset? SupporterUntil,
    bool Whitelisted,
    bool IsBot);

/// <summary>Reads accounts (groups, restriction, silence, supporter, whitelist) for trusted plugins.</summary>
public interface IHostAccounts
{
    /// <summary>The account, or null when there's none with that id (or it was deleted).</summary>
    /// <param name="userId">The account's id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account, or null.</returns>
    Task<HostAccount?> GetAsync(long userId, CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IHostChat.PostAsync"/> did.</summary>
public enum ChatPostResult
{
    /// <summary>Sent to the channel's players, the website and the chat log.</summary>
    Posted,

    /// <summary>No such channel.</summary>
    NoChannel,

    /// <summary>The account doesn't exist.</summary>
    NoAccount,

    /// <summary>The account is restricted or silenced: nothing was sent.</summary>
    Muted,

    /// <summary>Nothing left to say (empty, or only control characters).</summary>
    Empty,
}

/// <summary>Says something in a chat channel as a player, for chat relays (Discord, other networks).</summary>
public interface IHostChat
{
    /// <summary>
    /// Says <paramref name="text"/> in <paramref name="channel"/> as <paramref name="asUserId"/>: in the game, on the
    /// website and in the chat log, unless the account is restricted or silenced. <see cref="ChatRecordedEvent"/>
    /// carries <paramref name="originTag"/>, so the relay that posted it can leave its own message alone.
    /// </summary>
    /// <param name="channel">The channel, e.g. <c>#osu</c>.</param>
    /// <param name="asUserId">Who says it.</param>
    /// <param name="text">The message (one line; longer text is cut).</param>
    /// <param name="originTag">Where it comes from, e.g. <c>discord</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    Task<ChatPostResult> PostAsync(string channel, long asUserId, string text, string originTag, CancellationToken cancellationToken = default);
}

/// <summary>
/// A plugin's own small key/value store with expiry (namespaced by plugin id): in Redis when the server has it,
/// in memory otherwise (then it doesn't survive a restart). For marks, cooldowns and work in progress, not records.
/// </summary>
public interface IPluginStateStore
{
    /// <summary>The value, or null when unset or expired.</summary>
    /// <param name="key">The key (any text; the plugin id is added in front).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The value, or null.</returns>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Sets a value, for <paramref name="ttl"/> (null: until removed).</summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    /// <param name="ttl">How long it lasts, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    Task SetAsync(string key, string value, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    /// <summary>Removes a value.</summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether there was one.</returns>
    Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>A play's replay, as a complete .osr.</summary>
/// <param name="Data">The .osr file.</param>
/// <param name="FileName">A file name for it.</param>
/// <param name="UserId">Whose play it is.</param>
/// <param name="IsLazer">Whether it was played on lazer (the spectator server saved it).</param>
public sealed record HostReplay(byte[] Data, string FileName, long UserId, bool IsLazer);

/// <summary>Replays for trusted plugins (replay checks, renders).</summary>
public interface IHostReplays
{
    /// <summary>The replay of a website score (stable or lazer), or null when the play has none.</summary>
    /// <param name="websiteScoreId">The website's score id (<c>scores.id</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The replay, or null.</returns>
    Task<HostReplay?> GetByWebsiteScoreAsync(long websiteScoreId, CancellationToken cancellationToken = default);

    /// <summary>The replay of a TomYum score (a Relax/Autopilot or other mode score kept by TomYum), or null.</summary>
    /// <param name="scoreId">TomYum's score id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The replay, or null.</returns>
    Task<HostReplay?> GetByTomYumScoreAsync(long scoreId, CancellationToken cancellationToken = default);
}

/// <summary>A beatmap as plugins see it (<see cref="IHostBeatmaps"/>).</summary>
/// <param name="BeatmapId">The map's id.</param>
/// <param name="BeatmapSetId">Its set's id.</param>
/// <param name="Md5">Its file's MD5.</param>
/// <param name="Artist">The song's artist.</param>
/// <param name="Title">The song's title.</param>
/// <param name="Version">The difficulty's name.</param>
/// <param name="Creator">The mapper.</param>
/// <param name="Mode">Its own ruleset (0 osu!, 1 taiko, 2 catch, 3 mania).</param>
/// <param name="Status">osu! API status (-2 graveyard ... 1 ranked, 2 approved, 3 qualified, 4 loved).</param>
/// <param name="StarRating">Star rating without mods.</param>
/// <param name="MaxCombo">Max combo, 0 when unknown.</param>
/// <param name="LengthSeconds">Drain time in seconds.</param>
public sealed record HostBeatmap(
    long BeatmapId,
    long BeatmapSetId,
    string Md5,
    string Artist,
    string Title,
    string Version,
    string Creator,
    int Mode,
    int Status,
    double StarRating,
    int MaxCombo,
    int LengthSeconds);

/// <summary>Beatmaps and their files, for trusted plugins.</summary>
public interface IHostBeatmaps
{
    /// <summary>The map by id, or null.</summary>
    /// <param name="beatmapId">The map's id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The map, or null.</returns>
    Task<HostBeatmap?> GetAsync(long beatmapId, CancellationToken cancellationToken = default);

    /// <summary>The map by its file's MD5, or null.</summary>
    /// <param name="md5">The MD5.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The map, or null.</returns>
    Task<HostBeatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default);

    /// <summary>The map's .osu file (cached), or null when it can't be had.</summary>
    /// <param name="beatmapId">The map's id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The file, or null.</returns>
    Task<byte[]?> GetOsuFileAsync(long beatmapId, CancellationToken cancellationToken = default);
}

/// <summary>A play to work out pp for (<see cref="IHostPerformance"/>), in osu!(stable)'s terms.</summary>
/// <param name="BeatmapId">The map.</param>
/// <param name="Mode">0 osu!, 1 taiko, 2 catch, 3 mania (Relax/Autopilot: give the mod).</param>
/// <param name="Mods">Legacy mod bits.</param>
/// <param name="MaxCombo">The play's max combo.</param>
/// <param name="Count300">300s.</param>
/// <param name="Count100">100s.</param>
/// <param name="Count50">50s.</param>
/// <param name="CountMiss">Misses.</param>
/// <param name="CountGeki">Gekis (mania: MAX).</param>
/// <param name="CountKatu">Katus (mania: 200).</param>
/// <param name="Passed">Whether it passed.</param>
public sealed record HostPerformanceRequest(
    long BeatmapId,
    int Mode,
    int Mods,
    int MaxCombo,
    int Count300,
    int Count100,
    int Count50,
    int CountMiss,
    int CountGeki = 0,
    int CountKatu = 0,
    bool Passed = true);

/// <summary>pp and star rating with the server's own calculators (the same as scores get).</summary>
public interface IHostPerformance
{
    /// <summary>The play's pp, or null when it can't be worked out (unknown map, unsupported ruleset).</summary>
    /// <param name="request">The play.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>pp, or null.</returns>
    Task<double?> CalculateAsync(HostPerformanceRequest request, CancellationToken cancellationToken = default);

    /// <summary>pp for full combos at each accuracy (0-1), or null when unknown (BanchoBot's <c>/np</c> reply).</summary>
    /// <param name="beatmapId">The map.</param>
    /// <param name="mode">The ruleset (converted if needed).</param>
    /// <param name="mods">Legacy mod bits.</param>
    /// <param name="accuracies">The accuracies.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>pp for each, in order, or null.</returns>
    Task<IReadOnlyList<double>?> ForAccuraciesAsync(long beatmapId, int mode, int mods, IReadOnlyList<double> accuracies, CancellationToken cancellationToken = default);

    /// <summary>The map's star rating in <paramref name="mode"/> with <paramref name="mods"/>, or null when unknown.</summary>
    /// <param name="beatmapId">The map.</param>
    /// <param name="mode">The ruleset (converted if needed).</param>
    /// <param name="mods">Legacy mod bits.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The star rating, or null.</returns>
    Task<double?> StarRatingAsync(long beatmapId, int mode, int mods, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs a plugin's recurring work (<see cref="IPluginContext.Scheduler"/>): every handler is stopped when the plugin
/// is unloaded, and one that throws is logged and runs again next time. Runs never overlap.
/// </summary>
public interface IPluginScheduler
{
    /// <summary>Runs <paramref name="handler"/> every <paramref name="interval"/> (the first time after one interval).</summary>
    /// <param name="interval">How often (at least one second).</param>
    /// <param name="handler">The work; its token is cancelled when the plugin unloads.</param>
    /// <returns>A disposable that stops it.</returns>
    IDisposable Every(TimeSpan interval, Func<CancellationToken, Task> handler);
}

/// <summary>A custom lazer ruleset the server ranks (staff turn them on in the website's admin panel).</summary>
/// <param name="Id">The ruleset id its scores are stored with (10 and up).</param>
/// <param name="Slug">Its name on the website and in URLs (rulesets.info's slug, e.g. <c>tau</c>).</param>
/// <param name="Name">Its display name.</param>
/// <param name="Assembly">Its DLL's assembly name (e.g. <c>osu.Game.Rulesets.Tau</c>).</param>
/// <param name="Version">The pinned release, or null.</param>
/// <param name="Sha256">The pinned DLL's SHA-256 (plays submit only on that file).</param>
public sealed record HostCustomRuleset(int Id, string Slug, string Name, string Assembly, string? Version, string Sha256);

/// <summary>
/// The rulesets scores can have: osu!'s four (0-3) and the custom lazer rulesets turned on (ranked by score, no pp;
/// their scores are the website's, with <c>ScoreProcessedEvent.RulesetId</c> 10 and up).
/// </summary>
public interface IRulesetRegistry
{
    /// <summary>The custom rulesets turned on now (read again at most once a minute).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rulesets, by id.</returns>
    Task<IReadOnlyList<HostCustomRuleset>> CustomAsync(CancellationToken cancellationToken = default);

    /// <summary>A ruleset's name: osu!'s own for 0-3, a custom ruleset's for its id, or null when unknown.</summary>
    /// <param name="rulesetId">The ruleset id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The name, or null.</returns>
    Task<string?> NameAsync(int rulesetId, CancellationToken cancellationToken = default);
}
