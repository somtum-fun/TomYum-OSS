using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using TomYum.Bancho;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Bancho;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Admin;

/// <summary>
/// BanchoBot tells a player, in a private message, when they're restricted (with the reason from their
/// account history) and when the restriction is lifted, as osu! does. The message reaches them in-game
/// right away when they're online, and is kept in website chat, so they also see it there and at their
/// next login. Which restriction a player was told about is kept (Redis <c>tomyum:restriction-notice:{id}</c>,
/// in memory without Redis), so each is announced once whatever triggers the refresh.
/// </summary>
public sealed class RestrictionNotices
{
    private const string KeyPrefix = "tomyum:restriction-notice:";

    /// <summary>What BanchoBot tells a player who was restricted (osu!'s own wording).</summary>
    public const string RestrictedText =
        "Your account is currently in restricted mode. Please visit the osu! website for more information.";

    private readonly IServiceScopeFactory _scopes;
    private readonly PlayerRegistry _players;
    private readonly IConnectionMultiplexer? _redis;
    private readonly OsuWebChatBridge? _bridge;
    private readonly ILogger<RestrictionNotices> _logger;
    private readonly string _site;
    private readonly ConcurrentDictionary<int, string> _memory = new();

    /// <param name="scopes">For the website's database (the account history).</param>
    /// <param name="players">Online players, to deliver the message in-game.</param>
    /// <param name="site">The website, e.g. <c>https://osu.example.com</c> (links in the message).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="redis">Where announced restrictions are remembered (memory without it).</param>
    /// <param name="bridge">Website chat, to keep the message there (in-game only without it).</param>
    public RestrictionNotices(IServiceScopeFactory scopes, PlayerRegistry players, string site, ILogger<RestrictionNotices> logger,
        IConnectionMultiplexer? redis = null, OsuWebChatBridge? bridge = null)
    {
        _scopes = scopes;
        _players = players;
        _site = site.TrimEnd('/');
        _logger = logger;
        _redis = redis;
        _bridge = bridge;
    }

    /// <summary>
    /// Tells <paramref name="username"/> about a restriction (or its end) they weren't told about yet;
    /// does nothing otherwise. Never throws: a notice must not undo the moderation that caused it.
    /// </summary>
    public async Task NotifyAsync(int userId, string username, bool restricted, CancellationToken cancellationToken = default)
    {
        if (userId == BanchoBot.UserId)
            return;

        try
        {
            var told = await GetAsync(userId).ConfigureAwait(false);
            string text;
            string state;
            if (restricted)
            {
                var latest = await LatestRestrictionAsync(userId, cancellationToken).ConfigureAwait(false);
                state = string.Create(CultureInfo.InvariantCulture, $"r:{latest?.BanId ?? 0}");
                if (told == state)
                    return;
                text = RestrictedText;
            }
            else
            {
                if (told is null || !told.StartsWith("r:", StringComparison.Ordinal))
                    return;
                state = "lifted";
                text = "Your account is no longer restricted. Welcome back!";
            }

            Deliver(userId, username, text);
            await SetAsync(userId, state).ConfigureAwait(false);
            _logger.LogInformation("BanchoBot told {Username} (id {UserId}) their account is {State}.", username, userId, restricted ? "restricted" : "no longer restricted");
        }
#pragma warning disable CA1031 // the restriction itself already happened; a failed notice is only logged
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Couldn't tell {Username} (id {UserId}) about their restriction.", username, userId);
        }
    }

    private void Deliver(int userId, string username, string text)
    {
        if (_players.GetByUserId(userId) is { } player)
        {
            var writer = new BanchoPacketWriter(512);
            ChatEncoder.WriteMessage(writer, new BanchoMessage(BanchoBot.Name, text, player.Presence.Username, BanchoBot.UserId));
            player.Enqueue(writer.ToArray());
        }
        _bridge?.ForwardPrivateMessage(BanchoBot.UserId, username, text);
    }

    private async Task<OsuWebAccountHistory?> LatestRestrictionAsync(int userId, CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OsuWebDbContext>();
        return await db.AccountHistory.AsNoTracking()
            .Where(h => h.UserId == (uint)userId && h.BanStatus == OsuWebAccountHistory.Restriction)
            .OrderByDescending(h => h.BanId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> GetAsync(int userId)
    {
        if (_redis is null)
            return _memory.TryGetValue(userId, out var value) ? value : null;
        var stored = await _redis.GetDatabase().StringGetAsync(KeyPrefix + userId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        return stored.HasValue ? stored.ToString() : null;
    }

    private async Task SetAsync(int userId, string state)
    {
        if (_redis is null)
        {
            _memory[userId] = state;
            return;
        }
        await _redis.GetDatabase().StringSetAsync(KeyPrefix + userId.ToString(CultureInfo.InvariantCulture), state).ConfigureAwait(false);
    }
}
