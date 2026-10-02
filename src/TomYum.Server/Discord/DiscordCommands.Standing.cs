using System.Globalization;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Admin;

namespace TomYum.Server.Discord;

/// <summary>
/// Profile badges (<c>/addbadge</c>, <c>/removebadge</c>: administrators) and clearing a player's
/// account standing (<c>/clearstanding</c>: staff). Each is written to the staff log, so it shows
/// in the user log channel like the other staff actions.
/// </summary>
public sealed partial class DiscordCommands
{
    private static async Task<DiscordReply> AddBadgeAsync(IServiceProvider services, IDiscordLinkRepository links, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken)
    {
        var (admin, error) = await AdminCallerAsync(services, links, callerId, cancellationToken).ConfigureAwait(false);
        if (admin is null)
            return DiscordReply.Text(error!, isPrivate: true);
        if (services.GetService<OsuWebBadges>() is not { } badges)
            return DiscordReply.Text("Badges need the website (osu-web mode).", isPrivate: true);
        if (await StaffTargetAsync(services, links, options.GetValueOrDefault("player"), cancellationToken).ConfigureAwait(false) is not { } target)
            return DiscordReply.Text("No such player.", isPrivate: true);

        var description = options.GetValueOrDefault("description")?.Trim() ?? string.Empty;
        if (description.Length == 0)
            return DiscordReply.Text("Give the badge's description (its tooltip).", isPrivate: true);
        var url = options.GetValueOrDefault("url")?.Trim();
        if (!string.IsNullOrEmpty(url) && !(Uri.TryCreate(url, UriKind.Absolute, out var link) && (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp)))
            return DiscordReply.Text("The link should be a web address (https://…).", isPrivate: true);

        var (image, problem) = await services.GetRequiredService<BadgeImages>()
            .PrepareAsync(options.GetValueOrDefault("image") ?? string.Empty, options.GetValueOrDefault("name"), cancellationToken).ConfigureAwait(false);
        if (image is null)
            return DiscordReply.Text(problem!, isPrivate: true);
        if (!await badges.AddAsync(target.Id, image, description, url, cancellationToken).ConfigureAwait(false))
            return DiscordReply.Text($"**{DiscordFormat.Escape(target.Username)}** already has that badge.", isPrivate: true);

        await LogAsync(services, admin, "user.badge_add", target, $"Gave {target.Username} the badge \"{description}\" ({image})", cancellationToken).ConfigureAwait(false);
        return DiscordReply.Text($"Gave **{DiscordFormat.Escape(target.Username)}** the badge **{DiscordFormat.Escape(description)}**. It's on their profile now.", isPrivate: true);
    }

    private static async Task<DiscordReply> RemoveBadgeAsync(IServiceProvider services, IDiscordLinkRepository links, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken)
    {
        var (admin, error) = await AdminCallerAsync(services, links, callerId, cancellationToken).ConfigureAwait(false);
        if (admin is null)
            return DiscordReply.Text(error!, isPrivate: true);
        if (services.GetService<OsuWebBadges>() is not { } badges)
            return DiscordReply.Text("Badges need the website (osu-web mode).", isPrivate: true);
        if (await StaffTargetAsync(services, links, options.GetValueOrDefault("player"), cancellationToken).ConfigureAwait(false) is not { } target)
            return DiscordReply.Text("No such player.", isPrivate: true);

        var owned = await badges.ListAsync(target.Id, cancellationToken).ConfigureAwait(false);
        if (owned.Count == 0)
            return DiscordReply.Text($"**{DiscordFormat.Escape(target.Username)}** has no badges.", isPrivate: true);

        var which = options.GetValueOrDefault("badge")?.Trim() ?? string.Empty;
        var badge = FindBadge(owned, which);
        if (badge is null)
        {
            var list = string.Join('\n', owned.Select((b, i) => string.Create(CultureInfo.InvariantCulture,
                $"**{i + 1}.** {DiscordFormat.Escape(b.Description)} (`{b.Image}`)")));
            var ask = which.Length == 0 ? "Which one? Give its number, description or image:" : "No badge matches that. Their badges:";
            return DiscordReply.Text($"{ask}\n{list}", isPrivate: true);
        }

        await badges.RemoveAsync(target.Id, badge.Image, cancellationToken).ConfigureAwait(false);
        await LogAsync(services, admin, "user.badge_remove", target, $"Took the badge \"{badge.Description}\" ({badge.Image}) from {target.Username}", cancellationToken).ConfigureAwait(false);
        return DiscordReply.Text($"Took **{DiscordFormat.Escape(badge.Description)}** from **{DiscordFormat.Escape(target.Username)}**.", isPrivate: true);
    }

    /// <summary>A badge by its number in the list (1 = oldest), its image name or its description.</summary>
    public static OsuWebBadge? FindBadge(IReadOnlyList<OsuWebBadge> badges, string text)
    {
        ArgumentNullException.ThrowIfNull(badges);
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (text.Length == 0)
            return null;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return number >= 1 && number <= badges.Count ? badges[number - 1] : null;
        return badges.FirstOrDefault(b => string.Equals(b.Image, text, StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(b.Image, text + ".png", StringComparison.OrdinalIgnoreCase))
               ?? badges.FirstOrDefault(b => string.Equals(b.Description, text, StringComparison.OrdinalIgnoreCase))
               ?? (badges.Where(b => b.Description.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList() is [var only] ? only : null);
    }

    private static async Task<DiscordReply> ClearStandingAsync(IServiceProvider services, IDiscordLinkRepository links, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken)
    {
        var (staff, error) = await StaffCallerAsync(services, links, callerId, cancellationToken).ConfigureAwait(false);
        if (staff is null)
            return DiscordReply.Text(error!, isPrivate: true);
        if (services.GetService<OsuWebModeration>() is not { } moderation)
            return DiscordReply.Text("Account standing needs the website (osu-web mode).", isPrivate: true);
        if (await StaffTargetAsync(services, links, options.GetValueOrDefault("player"), cancellationToken).ConfigureAwait(false) is not { } target)
            return DiscordReply.Text("No such player.", isPrivate: true);
        if (MayNotModerate(staff, target) is { } refusal)
            return DiscordReply.Text(refusal, isPrivate: true);
        var reason = options.GetValueOrDefault("reason")?.Trim();
        if (string.IsNullOrEmpty(reason))
            return DiscordReply.Text("Give a reason.", isPrivate: true);

        var what = options.GetValueOrDefault("what") ?? "all";
        sbyte[] kinds = what switch
        {
            "silences" => [OsuWebAccountHistory.Silence],
            "restrictions" => [OsuWebAccountHistory.Restriction],
            "notes" => [OsuWebAccountHistory.Note],
            _ => [OsuWebAccountHistory.Note, OsuWebAccountHistory.Restriction, OsuWebAccountHistory.Silence],
        };
        var (outcome, removed) = await moderation.ClearStandingAsync(target.Id, kinds, cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case ModerationOutcome.UnknownUser:
                return DiscordReply.Text("No such player.", isPrivate: true);
            case ModerationOutcome.AlreadyRestricted:
                return DiscordReply.Text($"**{DiscordFormat.Escape(target.Username)}** is restricted: lift it with /unrestrict first, or clear only silences or notes.", isPrivate: true);
        }
        if (removed == 0)
            return DiscordReply.Text($"**{DiscordFormat.Escape(target.Username)}** has nothing like that on their account.", isPrivate: true);

        // A cleared silence ends now: tell the game.
        if (services.GetService<UserModerationSync>() is { } sync)
            await sync.RefreshAsync(target.Id, cancellationToken).ConfigureAwait(false);
        var label = StandingKinds.First(k => k.Value == what || (what is not ("silences" or "restrictions" or "notes") && k.Value == "all")).Name;
        await LogAsync(services, staff, "user.clear_standing", target,
            string.Create(CultureInfo.InvariantCulture, $"Cleared {target.Username}'s account standing ({label}, {removed} record(s)): {reason}"), cancellationToken).ConfigureAwait(false);
        return DiscordReply.Text(string.Create(CultureInfo.InvariantCulture,
            $"Cleared {removed} record(s) ({label}) from **{DiscordFormat.Escape(target.Username)}**'s account standing."), isPrivate: true);
    }

    /// <summary>The caller's game account when it's an administrator.</summary>
    private static async Task<(User? Admin, string? Error)> AdminCallerAsync(IServiceProvider services, IDiscordLinkRepository links, ulong callerId, CancellationToken cancellationToken)
    {
        var (staff, error) = await StaffCallerAsync(services, links, callerId, cancellationToken).ConfigureAwait(false);
        if (staff is null)
            return (null, error);
        return staff.Privileges.HasFlag(UserPrivileges.Administrator) ? (staff, null) : (null, "Only administrators can give or take badges.");
    }

    private static Task LogAsync(IServiceProvider services, User actor, string action, User target, string summary, CancellationToken cancellationToken) =>
        services.GetRequiredService<IStaffLogRepository>().AddAsync(new StaffLogEntry
        {
            CreatedAt = DateTime.UtcNow,
            ActorId = actor.Id,
            Action = action,
            TargetId = target.Id,
            Summary = summary.Length > 512 ? summary[..512] : summary,
            Result = "discord",
        }, cancellationToken);
}
