using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Performance;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Plugins.Abstractions;
using TomYum.Server.Admin;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Plugins;

/// <summary>
/// <see cref="IHostCommands"/>: a command line goes through BanchoBot as the account (a session made up for it, with
/// the account's own privileges), so the permission checks, staff log and in-game effect are the chat ones.
/// </summary>
internal sealed class HostCommands : IHostCommands
{
    private readonly IServiceProvider _services;

    public HostCommands(IServiceProvider services) => _services = services;

    public async Task<string?> RunAsAsync(long asUserId, string commandLine, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        if (asUserId is <= 0 or > int.MaxValue)
            return null;
        if (asUserId == BanchoBot.UserId)
            return await RunAsBotAsync(commandLine, cancellationToken).ConfigureAwait(false);

        User? user;
        using (var scope = _services.CreateScope())
            user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync((int)asUserId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted)
            return null;

        var presence = new BanchoPresence
        {
            UserId = user.Id,
            Username = user.Username,
            Privileges = PrivilegeMapper.ToBancho(user.Privileges),
            CountryCode = CountryCodes.ToNumeric(user.Country),
        };
        var caller = new OnlinePlayer("plugin-run-as:" + user.Id.ToString(CultureInfo.InvariantCulture), presence);
        return await _services.GetRequiredService<BanchoBot>()
            .TryHandleCommandAsync(caller, commandLine, presence.Privileges, cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> RunAsBotAsync(string commandLine, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var bot = BanchoBot.CreatePresence();
        var presence = new BanchoPresence
        {
            UserId = bot.UserId,
            Username = bot.Username,
            Privileges = bot.Privileges | BanchoPrivileges.Owner | BanchoPrivileges.Developer,
            CountryCode = bot.CountryCode,
        };
        var caller = new OnlinePlayer("plugin-run-as:bot", presence);
        return _services.GetRequiredService<BanchoBot>().TryHandleCommandAsync(caller, commandLine, presence.Privileges, cancellationToken);
    }
}

/// <summary><see cref="IHostAccounts"/> over TomYum's user repository and the website's groups, flags and supporter.</summary>
internal sealed class HostAccounts : IHostAccounts
{
    private static readonly IReadOnlySet<string> NoGroups = new HashSet<string>(StringComparer.Ordinal);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;

    public HostAccounts(IServiceScopeFactory scopes, TimeProvider? time = null)
    {
        _scopes = scopes;
        _time = time ?? TimeProvider.System;
    }

    public async Task<HostAccount?> GetAsync(long userId, CancellationToken cancellationToken = default)
    {
        if (userId is <= 0 or > int.MaxValue)
            return null;
        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        var user = await services.GetRequiredService<IUserRepository>().GetByIdAsync((int)userId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted)
            return null;

        var groups = services.GetService<OsuWebCommunity>() is { } community
            ? await community.GetGroupsAsync(user.Id, cancellationToken).ConfigureAwait(false)
            : NoGroups;
        var whitelisted = services.GetService<OsuWebAccountFlags>() is { } flags
            && await flags.IsWhitelistedAsync(user.Id, cancellationToken).ConfigureAwait(false);
        DateTimeOffset? supporterUntil = null;
        if (services.GetService<OsuWebDbContext>() is { } website)
        {
            var expiry = await website.Database
                .SqlQuery<DateTime?>($"SELECT osu_subscriptionexpiry AS Value FROM phpbb_users WHERE user_id = {user.Id} AND osu_subscriber = 1")
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (expiry is { } until && until > _time.GetUtcNow().UtcDateTime)
                supporterUntil = new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Utc));
        }

        var silenced = user.SilenceEndUtc is { } end && end > _time.GetUtcNow() ? end : (DateTimeOffset?)null;
        return new HostAccount(user.Id, user.Username, user.Country.ToLowerInvariant(), groups, user.IsRestricted, silenced, supporterUntil, whitelisted, user.IsBot);
    }
}

/// <summary>
/// <see cref="IHostChat"/>: what the Discord relay did for itself, for any relay: the channel's players, website
/// chat and the chat log (tagged with the relay's origin), unless the account is restricted or silenced.
/// </summary>
internal sealed class HostChat : IHostChat
{
    private const int MaxMessage = 400;

    private readonly IServiceScopeFactory _scopes;
    private readonly ChannelRegistry _channels;
    private readonly ChatLog _chatLog;
    private readonly OsuWebChatBridge? _website;
    private readonly TimeProvider _time;

    public HostChat(IServiceScopeFactory scopes, ChannelRegistry channels, ChatLog chatLog, OsuWebChatBridge? website = null, TimeProvider? time = null)
    {
        _scopes = scopes;
        _channels = channels;
        _chatLog = chatLog;
        _website = website;
        _time = time ?? TimeProvider.System;
    }

    public async Task<ChatPostResult> PostAsync(string channel, long asUserId, string text, string originTag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(originTag);
        if (ToLine(text) is not { } line)
            return ChatPostResult.Empty;
        if (_channels.Get(channel) is not { } target)
            return ChatPostResult.NoChannel;
        if (asUserId is <= 0 or > int.MaxValue)
            return ChatPostResult.NoAccount;

        User? user;
        using (var scope = _scopes.CreateScope())
            user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync((int)asUserId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.IsDeleted)
            return ChatPostResult.NoAccount;
        if (user.IsRestricted || user.SilenceEndUtc > _time.GetUtcNow())
            return ChatPostResult.Muted;

        var writer = new BanchoPacketWriter(64 + (line.Length * 2));
        ChatEncoder.WriteMessage(writer, new BanchoMessage(user.Username, line, target.Name, user.Id));
        var framed = writer.ToArray();
        foreach (var member in target.Members)
            member.Enqueue(framed);
        _chatLog.Record(user.Id, user.Username, target.Name, line, recipientId: null, ChatOrigin.External, originTag.Trim().ToLowerInvariant());
        _website?.ForwardChannelMessage(user.Id, target.Name, line);
        return ChatPostResult.Posted;
    }

    // One line, no control characters, cut to osu!'s length.
    private static string? ToLine(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        line = new string(line.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (line.Length == 0)
            return null;
        return line.Length > MaxMessage ? line[..(MaxMessage - 1)] + "…" : line;
    }
}

/// <summary>
/// <see cref="IHostReplays"/>: lazer plays' replays as the spectator server saved them, stable ones from TomYum's
/// replay data with the .osr header built from the score (as replay downloads and renders do).
/// </summary>
internal sealed class HostReplays : IHostReplays
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;

    public HostReplays(IServiceScopeFactory scopes, IConfiguration configuration)
    {
        _scopes = scopes;
        _configuration = configuration;
    }

    public async Task<HostReplay?> GetByWebsiteScoreAsync(long websiteScoreId, CancellationToken cancellationToken = default)
    {
        if (websiteScoreId <= 0)
            return null;
        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        if (services.GetService<OsuWebDbContext>() is not { } website)
            return null;
        var score = await website.Scores.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == (ulong)websiteScoreId, cancellationToken).ConfigureAwait(false);
        if (score is null)
            return null;

        if (score.LegacyScoreId is null)
        {
            if (ReplayFiles.LazerPathFor(_configuration, (ulong)websiteScoreId) is not { } lazer || !File.Exists(lazer))
                return null;
            var player = await NameAsync(services, (int)score.UserId, cancellationToken).ConfigureAwait(false);
            return new HostReplay(await File.ReadAllBytesAsync(lazer, cancellationToken).ConfigureAwait(false), FileName(player, websiteScoreId), score.UserId, IsLazer: true);
        }

        return services.GetService<OsuWebScoreMirror>() is { } mirror
            && await mirror.GetTomYumScoreIdAsync((ulong)websiteScoreId, cancellationToken).ConfigureAwait(false) is { } tomyumId
            ? await FromTomYumAsync(services, tomyumId, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<HostReplay?> GetByTomYumScoreAsync(long scoreId, CancellationToken cancellationToken = default)
    {
        if (scoreId <= 0)
            return null;
        using var scope = _scopes.CreateScope();
        return await FromTomYumAsync(scope.ServiceProvider, scoreId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HostReplay?> FromTomYumAsync(IServiceProvider services, long scoreId, CancellationToken cancellationToken)
    {
        var score = await services.GetRequiredService<TomYumDbContext>().Scores.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == scoreId, cancellationToken).ConfigureAwait(false);
        if (score is null || ReplayFiles.Find(_configuration, score) is not { } replay)
            return null;
        var player = await NameAsync(services, score.UserId, cancellationToken).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(replay.Path, cancellationToken).ConfigureAwait(false);
        return new HostReplay(replay.Complete ? bytes : OsrFile.Build(score, player, bytes), FileName(player, scoreId), score.UserId, IsLazer: false);
    }

    private static async Task<string> NameAsync(IServiceProvider services, int userId, CancellationToken cancellationToken) =>
        (await services.GetRequiredService<IUserRepository>().GetByIdAsync(userId, cancellationToken).ConfigureAwait(false))?.Username ?? "player";

    private static string FileName(string player, long id) =>
        string.Create(CultureInfo.InvariantCulture, $"{new string(player.Where(char.IsLetterOrDigit).ToArray())}-{id}.osr");
}

/// <summary><see cref="IHostBeatmaps"/> over TomYum's beatmaps (fetched when unknown) and the .osu file cache.</summary>
internal sealed class HostBeatmaps : IHostBeatmaps
{
    private readonly IServiceScopeFactory _scopes;
    private readonly OsuFileCache? _files;

    public HostBeatmaps(IServiceScopeFactory scopes, OsuFileCache? files = null)
    {
        _scopes = scopes;
        _files = files;
    }

    public async Task<HostBeatmap?> GetAsync(long beatmapId, CancellationToken cancellationToken = default)
    {
        if (beatmapId is <= 0 or > int.MaxValue)
            return null;
        using var scope = _scopes.CreateScope();
        var map = await scope.ServiceProvider.GetRequiredService<IBeatmapRepository>().GetByIdAsync((int)beatmapId, cancellationToken).ConfigureAwait(false);
        return map is null ? null : ToHost(map);
    }

    public async Task<HostBeatmap?> GetByMd5Async(string md5, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(md5);
        if (md5.Length != 32)
            return null;
        using var scope = _scopes.CreateScope();
        var map = scope.ServiceProvider.GetService<BeatmapService>() is { } service
            ? await service.GetByMd5Async(md5.ToLowerInvariant(), cancellationToken).ConfigureAwait(false)
            : await scope.ServiceProvider.GetRequiredService<IBeatmapRepository>().GetByMd5Async(md5.ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
        return map is null ? null : ToHost(map);
    }

    public async Task<byte[]?> GetOsuFileAsync(long beatmapId, CancellationToken cancellationToken = default)
    {
        if (_files is null || beatmapId is <= 0 or > int.MaxValue)
            return null;
        try
        {
            var bytes = await _files.GetAsync((int)beatmapId, cancellationToken).ConfigureAwait(false);
            return bytes.Length == 0 ? null : bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static HostBeatmap ToHost(Beatmap map) => new(
        map.Id, map.SetId, map.Md5, map.Artist, map.Title, map.Version, map.Creator, map.Mode,
        CustomBeatmapCatalog.ToApiStatus(map.Status), map.StarRating, map.MaxCombo, map.TotalLength);
}

/// <summary><see cref="IHostPerformance"/> over the server's own pp calculator (the routing one scores use).</summary>
internal sealed class HostPerformance : IHostPerformance
{
    private readonly IPerformanceCalculator _calculator;

    public HostPerformance(IPerformanceCalculator calculator) => _calculator = calculator;

    public async Task<double?> CalculateAsync(HostPerformanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BeatmapId is <= 0 or > int.MaxValue || request.Mode is < 0 or > 3)
            return null;
        var pp = await _calculator.CalculateAsync(new PerformanceRequest(
            (int)request.BeatmapId, request.Mode, request.Mods, request.MaxCombo,
            request.Count300, request.Count100, request.Count50, request.CountMiss, request.CountGeki, request.CountKatu,
            request.Passed), cancellationToken).ConfigureAwait(false);
        return double.IsFinite(pp) && pp >= 0 ? pp : null;
    }

    public Task<IReadOnlyList<double>?> ForAccuraciesAsync(long beatmapId, int mode, int mods, IReadOnlyList<double> accuracies, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accuracies);
        return _calculator is IAccuracyPerformance withAccuracies && beatmapId is > 0 and <= int.MaxValue
            ? withAccuracies.ForAccuraciesAsync((int)beatmapId, mode, mods, accuracies, cancellationToken)
            : Task.FromResult<IReadOnlyList<double>?>(null);
    }

    public Task<double?> StarRatingAsync(long beatmapId, int mode, int mods, CancellationToken cancellationToken = default) =>
        _calculator is IAccuracyPerformance withStars && beatmapId is > 0 and <= int.MaxValue
            ? withStars.StarRatingAsync((int)beatmapId, mode, mods, cancellationToken)
            : Task.FromResult<double?>(null);
}

/// <summary>
/// <see cref="IRulesetRegistry"/> over the website's <c>tomyum_custom_rulesets</c> (osu-web <c>TomYumCustomRulesets</c>),
/// read again at most once a minute.
/// </summary>
internal sealed class HostRulesetRegistry : IRulesetRegistry, IDisposable
{
    private static readonly string[] Official = ["osu!", "osu!taiko", "osu!catch", "osu!mania"];
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (DateTimeOffset At, IReadOnlyList<HostCustomRuleset> Rulesets)? _cached;

    public HostRulesetRegistry(IServiceScopeFactory scopes, TimeProvider? time = null)
    {
        _scopes = scopes;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<HostCustomRuleset>> CustomAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } fresh && _time.GetUtcNow() - fresh.At < Keep)
            return fresh.Rulesets;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is { } again && _time.GetUtcNow() - again.At < Keep)
                return again.Rulesets;

            IReadOnlyList<HostCustomRuleset> rulesets = [];
            using var scope = _scopes.CreateScope();
            if (scope.ServiceProvider.GetService<OsuWebDbContext>() is { } website)
            {
                try
                {
                    rulesets = (await website.Database.SqlQueryRaw<RulesetRow>(
                            "SELECT CAST(id AS SIGNED) AS Id, slug AS Slug, name AS Name, assembly AS Assembly, version AS Version, sha256 AS Sha256 FROM tomyum_custom_rulesets WHERE enabled = 1 ORDER BY id")
                        .ToListAsync(cancellationToken).ConfigureAwait(false))
                        .Select(r => new HostCustomRuleset((int)r.Id, r.Slug, r.Name, r.Assembly, r.Version, r.Sha256))
                        .ToList();
                }
                catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.NoSuchTable)
                {
                    // the website hasn't made the table yet
                }
            }

            _cached = (_time.GetUtcNow(), rulesets);
            return rulesets;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> NameAsync(int rulesetId, CancellationToken cancellationToken = default)
    {
        if (rulesetId is >= 0 and <= 3)
            return Official[rulesetId];
        return (await CustomAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(r => r.Id == rulesetId)?.Name;
    }

    public void Dispose() => _lock.Dispose();

    private sealed record RulesetRow(long Id, string Slug, string Name, string Assembly, string? Version, string Sha256);
}
