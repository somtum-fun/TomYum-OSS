using System.Globalization;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.Discord;

/// <summary>
/// lazer's main-menu banners (<c>/banner_add</c>, <c>/banner_remove</c>: administrators), the same
/// list as the admin panel's Menu banners page (<see cref="MenuBanners"/>). Each is written to the staff log.
/// </summary>
public sealed partial class DiscordCommands
{
    private const string BannerRefusal = "Only administrators can change the menu banners.";

    private static async Task<DiscordReply> AddMenuBannerAsync(IServiceProvider services, IDiscordLinkRepository links, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken)
    {
        var (admin, error) = await AdminCallerAsync(services, links, callerId, cancellationToken, BannerRefusal).ConfigureAwait(false);
        if (admin is null)
            return DiscordReply.Text(error!, isPrivate: true);

        DateTimeOffset? expires = null;
        if (options.GetValueOrDefault("expires")?.Trim() is { Length: > 0 } day)
        {
            if (!DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                return DiscordReply.Text("Give the end as a day, like 2026-11-01.", isPrivate: true);
            expires = new DateTimeOffset(date, TimeSpan.Zero);
            if (expires <= DateTimeOffset.UtcNow)
                return DiscordReply.Text("That day has already passed.", isPrivate: true);
        }

        var image = options.GetValueOrDefault("image_url")?.Trim() ?? string.Empty;
        var url = options.GetValueOrDefault("click_url")?.Trim() ?? string.Empty;
        if (await services.GetRequiredService<MenuBanners>().AddAsync(image, url, null, expires, cancellationToken).ConfigureAwait(false) is { } problem)
            return DiscordReply.Text(problem, isPrivate: true);

        await LogBannerAsync(services, admin, "menu_banner.add", $"Added a menu banner ({image}, linking to {url})", cancellationToken).ConfigureAwait(false);
        var until = expires is { } end ? $" until {end.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}" : string.Empty;
        return new DiscordReply(new DiscordPost(Embed: new DiscordEmbed
        {
            Title = "Menu banner added",
            Description = $"It's on lazer's main menu{until} (players see it the next time the menu checks, within a few minutes).\nClicking it opens {url}",
            ImageUrl = image,
            Color = DiscordFormat.Accent,
        }), true);
    }

    private static async Task<DiscordReply> RemoveMenuBannerAsync(IServiceProvider services, IDiscordLinkRepository links, IReadOnlyDictionary<string, string> options, ulong callerId, CancellationToken cancellationToken)
    {
        var (admin, error) = await AdminCallerAsync(services, links, callerId, cancellationToken, BannerRefusal).ConfigureAwait(false);
        if (admin is null)
            return DiscordReply.Text(error!, isPrivate: true);

        var store = services.GetRequiredService<MenuBanners>();
        var banners = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        if (banners.Count == 0)
            return DiscordReply.Text("There are no menu banners.", isPrivate: true);

        var which = options.GetValueOrDefault("banner")?.Trim() ?? string.Empty;
        if (!int.TryParse(which, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > banners.Count)
        {
            var now = DateTimeOffset.UtcNow;
            var list = string.Join('\n', banners.Select((b, i) => string.Create(CultureInfo.InvariantCulture,
                $"**{i + 1}.** <{b.Image}> → <{b.Url}>{(MenuBanners.IsCurrent(b, now) ? string.Empty : " (not showing now)")}")));
            var ask = which.Length == 0 ? "Which one? Give its number:" : "No banner has that number. The banners:";
            return DiscordReply.Text($"{ask}\n{list}", isPrivate: true);
        }

        if (await store.RemoveAsync(number - 1, cancellationToken).ConfigureAwait(false) is not { } removed)
            return DiscordReply.Text("That banner was just removed by someone else.", isPrivate: true);
        await LogBannerAsync(services, admin, "menu_banner.remove", $"Removed a menu banner ({removed.Image})", cancellationToken).ConfigureAwait(false);
        return DiscordReply.Text($"Took banner {number.ToString(CultureInfo.InvariantCulture)} off the menu (<{removed.Image}>).", isPrivate: true);
    }

    private static Task LogBannerAsync(IServiceProvider services, User actor, string action, string summary, CancellationToken cancellationToken) =>
        services.GetRequiredService<IStaffLogRepository>().AddAsync(new StaffLogEntry
        {
            CreatedAt = DateTime.UtcNow,
            ActorId = actor.Id,
            Action = action,
            Summary = summary.Length > 512 ? summary[..512] : summary,
            Result = "discord",
        }, cancellationToken);
}
