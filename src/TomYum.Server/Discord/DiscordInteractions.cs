using System.Globalization;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Server.Admin;
using TomYum.Server.Bancho;

namespace TomYum.Server.Discord;

/// <summary>
/// What the bot's buttons do. Every action runs as the presser's linked game account, with the same
/// checks as the command it stands for:
/// <list type="bullet">
/// <item><c>status:{status}:{set}:{beatmap|0}</c>: confirms <c>/rankmap</c>, <c>/lovemap</c>, <c>/qualify</c> (BN, NAT, admins);</item>
/// <item><c>mod:{restrict|silence|unrestrict|dismiss}:{user}:{what}</c>: acts on a report, a suspicious play or a circleguard restriction (staff);</item>
/// <item><c>rs:{user}:{ruleset}:{index}:{slot}</c>: pages <c>/recent</c> (anyone);</item>
/// <item><c>cancel</c>: closes a confirmation.</item>
/// </list>
/// </summary>
public sealed class DiscordInteractions
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ModerationCommands? _moderation;
    private readonly DiscordCommands? _commands;

    public DiscordInteractions(IServiceScopeFactory scopes, ModerationCommands? moderation = null, DiscordCommands? commands = null)
    {
        _scopes = scopes;
        _moderation = moderation;
        _commands = commands;
    }

    /// <summary>Handles a button pressed by Discord user <paramref name="presserId"/>.</summary>
    public async Task<DiscordButtonResult> PressAsync(string customId, ulong presserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customId);
        if (customId == "cancel")
            return new DiscordButtonResult(new DiscordPost("Cancelled."), null);

        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        if (customId.StartsWith("rs:", StringComparison.Ordinal))
            return await RecentPageAsync(services, customId.Split(':'), cancellationToken).ConfigureAwait(false);
        var links = services.GetRequiredService<IDiscordLinkRepository>();
        if (await links.GetByDiscordIdAsync(presserId, cancellationToken).ConfigureAwait(false) is not { } link)
            return DiscordButtonResult.Say("Link your account first with /link: buttons act as your game account.");
        var users = services.GetRequiredService<IUserRepository>();
        if (await users.GetByIdAsync(link.UserId, cancellationToken).ConfigureAwait(false) is not { } presser)
            return DiscordButtonResult.Say("Your linked account is gone.");

        var parts = customId.Split(':');
        return parts[0] switch
        {
            "status" when parts.Length == 4 => await StatusAsync(services, presser, parts, cancellationToken).ConfigureAwait(false),
            "mod" when parts.Length == 4 => await ModerateAsync(users, presser, parts, cancellationToken).ConfigureAwait(false),
            _ => DiscordButtonResult.Say("That button doesn't do anything anymore."),
        };
    }

    /// <summary>Another of the player's recent plays, in place (restricted players' aren't shown).</summary>
    private async Task<DiscordButtonResult> RecentPageAsync(IServiceProvider services, string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length < 4
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ruleset)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || services.GetService<Persistence.OsuWeb.OsuWebCommunity>() is not { } community
            || (_commands ?? services.GetService<DiscordCommands>()) is not { } commands)
            return DiscordButtonResult.Say("That button doesn't do anything anymore.");

        var player = await community.GetPlayerAsync(userId, ruleset, cancellationToken).ConfigureAwait(false);
        if (player is null || player.IsRestricted
            || await commands.RecentCardAsync(services, community, player, index, cancellationToken).ConfigureAwait(false) is not { } card)
            return DiscordButtonResult.Say("There's nothing to show there.");
        return new DiscordButtonResult(card, null);
    }

    private static async Task<DiscordButtonResult> StatusAsync(IServiceProvider services, User presser, string[] parts, CancellationToken cancellationToken)
    {
        if (!await MapCommands.MayChangeStatusAsync(services, presser.Id, cancellationToken).ConfigureAwait(false))
            return DiscordButtonResult.Say("That's for the Beatmap Nominators, NAT and admins.");
        if (!BeatmapStatusChanger.Statuses.TryGetValue(parts[1], out var status)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var setId)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var beatmapId))
            return DiscordButtonResult.Say("That button is broken; run the command again.");
        if (services.GetService<BeatmapStatusChanger>() is not { } changer)
            return DiscordButtonResult.Say("Status changes need the website (osu-web mode).");

        IReadOnlyCollection<int>? only = beatmapId > 0 ? [beatmapId] : null;
        var result = await changer.ChangeAsync(setId, status, only, presser.Id, cancellationToken).ConfigureAwait(false);
        if (result is null)
            return new DiscordButtonResult(new DiscordPost("That set isn't on the server anymore."), null);
        if (result.Changed.Count == 0)
            return new DiscordButtonResult(new DiscordPost($"Nothing changed: it's already {parts[1]}."), null);

        var recalculated = result.Scores is { Changed: > 0 } scores
            ? string.Create(CultureInfo.InvariantCulture, $" {scores.Changed} score(s) were recalculated.")
            : string.Empty;
        return new DiscordButtonResult(new DiscordPost(
            string.Create(CultureInfo.InvariantCulture, $"✅ {result.Changed.Count} difficulty(ies) → **{parts[1]}**.{recalculated} It's announced in the beatmaps channel.")), null);
    }

    /// <summary>A report's or suspicious play's buttons: restrict, silence for a day, or dismiss.</summary>
    private async Task<DiscordButtonResult> ModerateAsync(IUserRepository users, User presser, string[] parts, CancellationToken cancellationToken)
    {
        if (!ModerationCommands.IsStaff(presser.Privileges) || presser.IsRestricted)
            return DiscordButtonResult.Say("That's staff-only.");
        var who = DiscordFormat.Escape(presser.Username);
        if (parts[1] == "dismiss")
            return DiscordButtonResult.Handled($"Dismissed by {who} {DiscordFormat.Relative(DateTime.UtcNow)}");

        if (_moderation is null)
            return DiscordButtonResult.Say("Moderation isn't available on this server.");
        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var userId)
            || await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false) is not { } target)
            return DiscordButtonResult.Say("That player is gone.");

        var reason = parts[3];
        var (command, arguments, done) = parts[1] switch
        {
            "restrict" => ("restrict", new[] { target.Username, reason }, "Restricted"),
            "silence" => ("silence", new[] { target.Username, "1d", reason }, "Silenced for a day"),
            "unrestrict" => ("unrestrict", new[] { target.Username, reason }, "Unrestricted"),
            _ => (string.Empty, Array.Empty<string>(), string.Empty),
        };
        if (command.Length == 0)
            return DiscordButtonResult.Say("That button doesn't do anything anymore.");

        // Same path as the command: permission checks, the staff log, applied in-game at once.
        var reply = await _moderation.RunAsync(presser.Id, command, arguments, cancellationToken).ConfigureAwait(false);
        return reply.StartsWith(done.Split(' ')[0], StringComparison.Ordinal)
            ? DiscordButtonResult.Handled($"{done} by {who} {DiscordFormat.Relative(DateTime.UtcNow)}")
            : DiscordButtonResult.Say(reply);
    }
}
