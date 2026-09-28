using System.Globalization;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Admin;

namespace TomYum.Server.Bancho;

/// <summary>
/// BanchoBot's moderation commands: <c>!restrict</c>/<c>!ban</c>, <c>!unrestrict</c>/<c>!unban</c>,
/// <c>!silence</c>, <c>!unsilence</c>, <c>!alert</c>, <c>!alertu</c>, and <c>!alts</c>/<c>!verify</c> for
/// the ban-evasion check (accounts sharing a machine; letting a refused new account in). They do what the website's
/// GMT panel does (the same account rows, the same staff log, applied in-game at once), so staff
/// can use either. Staff = moderators (GMT), administrators and developers, checked against their
/// account as stored now, not the session's privileges; like the panel, only administrators may
/// act on other staff.
/// </summary>
public sealed class ModerationCommands
{
    /// <summary>The longest silence a command can give.</summary>
    public static readonly TimeSpan MaxSilence = TimeSpan.FromDays(365);

    private readonly IServiceScopeFactory _scopes;
    private readonly PlayerRegistry _players;
    private readonly TimeProvider _time;
    private readonly OsuWeb.OsuWebChatBridge? _website;

    public ModerationCommands(IServiceScopeFactory scopes, PlayerRegistry players, TimeProvider? time = null, OsuWeb.OsuWebChatBridge? website = null)
    {
        _scopes = scopes;
        _players = players;
        _time = time ?? TimeProvider.System;
        _website = website;
    }

    /// <summary>An alert as a chat message (lazer and the website have no pop-up for it).</summary>
    private static string WebsiteAlert(string text) => $"📢 Staff alert: {text}";

    /// <summary>The moderation command words.</summary>
    public static bool Handles(string command) =>
        command is "restrict" or "ban" or "unrestrict" or "unban" or "silence" or "unsilence" or "alert" or "alertu" or "verify" or "alts";

    /// <summary>Runs <paramref name="command"/> for <paramref name="callerId"/>; <paramref name="parts"/> are the words after the command.</summary>
    public async Task<string> RunAsync(int callerId, string command, string[] parts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parts);

        using var scope = _scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var caller = await users.GetByIdAsync(callerId, cancellationToken).ConfigureAwait(false);
        if (caller is null || !IsStaff(caller.Privileges))
            return "That command is staff-only.";

        switch (command)
        {
            case "alert":
            {
                if (parts.Length == 0)
                    return "Usage: !alert <message>";
                var text = string.Join(' ', parts);
                var everyone = Notification(text);
                var inGame = _players.All.Where(p => p.UserId != BanchoBot.UserId).ToList();
                foreach (var player in inGame)
                    player.Enqueue(everyone);

                // Lazer (and website) players get it as a message from BanchoBot: that's what reaches them.
                var elsewhere = 0;
                if (scope.ServiceProvider.GetService<OsuWebCommunity>() is { } community && _website is not null)
                {
                    var stable = inGame.Select(p => p.UserId).ToHashSet();
                    foreach (var (userId, username) in await community.GetOnlineAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (stable.Contains(userId))
                            continue;
                        _website.ForwardPrivateMessage(BanchoBot.UserId, username, WebsiteAlert(text));
                        elsewhere++;
                    }
                }
                return elsewhere > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"Sent to {inGame.Count} player(s) in-game and {elsewhere} on lazer/the website.")
                    : string.Create(CultureInfo.InvariantCulture, $"Sent to {inGame.Count} player(s).");
            }

            case "alertu":
            {
                if (parts.Length < 2)
                    return "Usage: !alertu <user> <message>";
                var text = string.Join(' ', parts[1..]);
                var recipient = _players.GetByUsername(parts[0].Replace('_', ' '))
                    ?? _players.All.FirstOrDefault(p => User.MakeSafeName(p.Presence.Username) == User.MakeSafeName(parts[0]));
                if (recipient is not null)
                {
                    recipient.Enqueue(Notification(text));
                    return $"Sent to {recipient.Presence.Username}.";
                }

                // Not in stable: as a message on lazer/the website, where they'll see it next.
                if (_website is not null && await users.GetBySafeNameAsync(User.MakeSafeName(parts[0]), cancellationToken).ConfigureAwait(false) is { } user)
                {
                    _website.ForwardPrivateMessage(BanchoBot.UserId, user.Username, WebsiteAlert(text));
                    return $"Sent to {user.Username} as a message (they're not in stable).";
                }
                return $"{parts[0]} isn't online.";
            }

            case "alts" or "verify":
                return await RunHardwareCommandAsync(scope.ServiceProvider, users, caller, command, parts, cancellationToken).ConfigureAwait(false);
        }

        var moderation = scope.ServiceProvider.GetService<OsuWebModeration>();
        if (moderation is null)
            return "Moderation needs the website's accounts (osu-web mode).";

        var usage = Usage(command);
        var minimum = command is "silence" ? 3 : command is "unsilence" ? 1 : 2;
        if (parts.Length < minimum)
            return usage;

        var target = await users.GetBySafeNameAsync(User.MakeSafeName(parts[0]), cancellationToken).ConfigureAwait(false);
        if (target is null)
            return $"No such user \"{parts[0]}\".";
        if (target.Id == BanchoBot.UserId)
            return "BanchoBot can't be moderated.";
        if (target.Id != caller.Id && IsStaff(target.Privileges) && !caller.Privileges.HasFlag(UserPrivileges.Administrator))
            return "Only administrators can moderate other staff.";

        string reason;
        ModerationOutcome outcome;
        string done;
        switch (command)
        {
            case "restrict" or "ban":
                reason = string.Join(' ', parts[1..]);
                outcome = await moderation.RestrictAsync(target.Id, caller.Id, reason, cancellationToken).ConfigureAwait(false);
                done = $"Restricted {target.Username}: {reason}";
                break;
            case "unrestrict" or "unban":
                reason = string.Join(' ', parts[1..]);
                outcome = await moderation.UnrestrictAsync(target.Id, caller.Id, reason, cancellationToken).ConfigureAwait(false);
                done = $"Unrestricted {target.Username}: {reason}";
                break;
            case "silence":
                if (!TryParseDuration(parts[1], out var length))
                    return "Length like 10m, 1h, 6h, 1d, 1w (up to 365d).";
                reason = string.Join(' ', parts[2..]);
                outcome = await moderation.SilenceAsync(target.Id, caller.Id, length, reason, cancellationToken).ConfigureAwait(false);
                done = $"Silenced {target.Username} for {Describe(length)}: {reason}";
                break;
            default: // unsilence
                reason = parts.Length > 1 ? string.Join(' ', parts[1..]) : "Lifted early";
                outcome = await moderation.UnsilenceAsync(target.Id, caller.Id, reason, cancellationToken).ConfigureAwait(false);
                done = $"Lifted {target.Username}'s silence: {reason}";
                break;
        }

        switch (outcome)
        {
            case ModerationOutcome.AlreadyRestricted:
                return $"{target.Username} is already restricted.";
            case ModerationOutcome.NotRestricted:
                return $"{target.Username} isn't restricted (a banned account is changed from the admin panel).";
            case ModerationOutcome.NotSilenced:
                return $"{target.Username} isn't silenced.";
            case ModerationOutcome.UnknownUser:
                return $"No such user \"{parts[0]}\".";
        }

        // Same trail and effect as the panel: the staff log, then applied in-game right away.
        await scope.ServiceProvider.GetRequiredService<IStaffLogRepository>().AddAsync(new StaffLogEntry
        {
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            ActorId = caller.Id,
            Action = command switch { "restrict" or "ban" => "user.restrict", "unrestrict" or "unban" => "user.unrestrict", _ => $"user.{command}" },
            TargetId = target.Id,
            Summary = done.Length > 512 ? done[..512] : done,
        }, cancellationToken).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<UserModerationSync>().RefreshAsync(target.Id, cancellationToken).ConfigureAwait(false);
        if (command is "restrict" or "ban" or "unrestrict" or "unban")
            await RecalculateCountryAsync(scope.ServiceProvider, target.Id, cancellationToken).ConfigureAwait(false);

        return done + ".";
    }

    /// <summary><c>!alts &lt;user&gt;</c> (other accounts seen on the same machine) and <c>!verify &lt;user&gt;</c>.</summary>
    private async Task<string> RunHardwareCommandAsync(IServiceProvider services, IUserRepository users, User caller, string command, string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length < 1)
            return $"Usage: !{command} <user>";
        var target = await users.GetBySafeNameAsync(User.MakeSafeName(parts[0]), cancellationToken).ConfigureAwait(false);
        if (target is null)
            return $"No such user \"{parts[0]}\".";
        var hashes = services.GetRequiredService<IClientHashRepository>();

        if (command == "verify")
        {
            if (!await hashes.VerifyAsync(target.Id, caller.Id, _time.GetUtcNow().UtcDateTime, cancellationToken).ConfigureAwait(false))
                return $"{target.Username} is already verified.";
            var summary = $"Verified {target.Username}: they can log in even if they share a machine with a restricted account";
            await services.GetRequiredService<IStaffLogRepository>().AddAsync(new StaffLogEntry
            {
                CreatedAt = _time.GetUtcNow().UtcDateTime,
                ActorId = caller.Id,
                Action = "user.verify",
                TargetId = target.Id,
                Summary = summary,
            }, cancellationToken).ConfigureAwait(false);
            return summary + ".";
        }

        var others = new List<int>();
        foreach (var seen in await hashes.ListAsync(target.Id, cancellationToken).ConfigureAwait(false))
        {
            var query = new ClientHashQuery(ClientHashMatch.Usable(seen.Adapters), ClientHashMatch.Usable(seen.UninstallId), ClientHashMatch.Usable(seen.DiskSerial));
            foreach (var id in await hashes.FindOtherUsersAsync(target.Id, query, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (!others.Contains(id))
                    others.Add(id);
            }
        }
        if (others.Count == 0)
            return $"No other accounts seen on {target.Username}'s machine(s).";

        var names = new List<string>();
        foreach (var id in others.Take(10))
        {
            var other = await users.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
            if (other is not null)
                names.Add(other.IsRestricted ? $"{other.Username} (restricted)" : other.Username);
        }
        var more = others.Count > 10 ? string.Create(CultureInfo.InvariantCulture, $" and {others.Count - 10} more") : string.Empty;
        return $"Same machine as {target.Username}: {string.Join(", ", names)}{more}.";
    }

    /// <summary>Moderators (GMT), administrators and developers.</summary>
    public static bool IsStaff(UserPrivileges privileges) =>
        (privileges & (UserPrivileges.Moderator | UserPrivileges.Administrator | UserPrivileges.Developer)) != 0;

    /// <summary>Parses <c>30s</c>, <c>10m</c>, <c>1h</c>, <c>1d</c>, <c>1w</c> (or bare seconds), up to <see cref="MaxSilence"/>.</summary>
    public static bool TryParseDuration(string text, out TimeSpan length)
    {
        length = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var unit = char.ToLowerInvariant(text[^1]);
        var number = char.IsDigit(unit) ? text : text[..^1];
        if (!long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            return false;

        var seconds = unit switch
        {
            's' => value,
            'm' => value * 60,
            'h' => value * 3600,
            'd' => value * 86400,
            'w' => value * 604800,
            _ when char.IsDigit(unit) => value,
            _ => -1,
        };
        if (seconds <= 0 || seconds > MaxSilence.TotalSeconds)
            return false;

        length = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static string Describe(TimeSpan length) => length switch
    {
        { TotalDays: >= 7 } when length.TotalDays % 7 == 0 => $"{length.TotalDays / 7:0} week(s)",
        { TotalDays: >= 1 } when length.TotalHours % 24 == 0 => $"{length.TotalDays:0} day(s)",
        { TotalHours: >= 1 } when length.TotalMinutes % 60 == 0 => $"{length.TotalHours:0} hour(s)",
        { TotalMinutes: >= 1 } when length.TotalSeconds % 60 == 0 => $"{length.TotalMinutes:0} minute(s)",
        _ => $"{length.TotalSeconds:0} second(s)",
    };

    private static string Usage(string command) => command switch
    {
        "restrict" or "ban" => $"Usage: !{command} <user> <reason>",
        "unrestrict" or "unban" => $"Usage: !{command} <user> <reason>",
        "silence" => "Usage: !silence <user> <length, e.g. 10m/1h/1d> <reason>",
        _ => "Usage: !unsilence <user> [reason]",
    };

    private static byte[] Notification(string text)
    {
        var writer = new BanchoPacketWriter(64 + (text.Length * 2));
        writer.WriteStringPacket(ServerPacketId.Notification, text);
        return writer.ToArray();
    }

    /// <summary>Restricted players leave their country's ranking (and come back), as with the panel.</summary>
    private static async Task RecalculateCountryAsync(IServiceProvider services, int userId, CancellationToken cancellationToken)
    {
        if (services.GetService<OsuWebScoreMirror>() is not { } mirror || services.GetService<OsuWeb.OsuWebInterop>() is not { IsConfigured: true } interop)
            return;
        try
        {
            if (await mirror.GetCountryAsync(userId, cancellationToken).ConfigureAwait(false) is { Length: > 0 } country)
                await interop.RecalculateCountryAsync(country, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Country rankings catch up on the next score.
        }
    }
}
