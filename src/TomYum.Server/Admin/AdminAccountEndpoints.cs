using Microsoft.EntityFrameworkCore;
using TomYum.Bancho;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;
using TomYum.Persistence;
using TomYum.Server.Bancho;

namespace TomYum.Server.Admin;

/// <summary>Another account seen on the same device.</summary>
public sealed record DeviceUser(int Id, string Username);

/// <summary>One hardware fingerprint an account logged in with (osu!stable's client hashes).</summary>
public sealed record AccountDevice(
    string OsuPath, string Adapters, string UninstallId, string DiskSerial,
    DateTime LastUsed, int Logins, IReadOnlyList<DeviceUser> OtherUsers);

/// <summary>The account's Discord and Last.fm links.</summary>
public sealed record AccountLinks(AccountDiscordLink? Discord, AccountLastFmLink? LastFm);

public sealed record AccountDiscordLink(string DiscordId, DateTime LinkedAt);

public sealed record AccountLastFmLink(string Username, DateTime LinkedAt);

/// <summary>Body of <c>POST /_admin/users/{id}/disconnect</c>: what the player is told.</summary>
public sealed record DisconnectRequest(string? Message);

/// <summary>
/// The admin panel's account tools: hardware fingerprints and who else used them, kicking a stable
/// session (after a lock or a device wipe, so it applies now), and connected profiles.
/// </summary>
public static class AdminAccountEndpoints
{
    public static void MapAccounts(this RouteGroupBuilder admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        admin.MapGet("/users/{userId:int}/devices", DevicesAsync);
        admin.MapPost("/users/{userId:int}/disconnect", Disconnect);
        admin.MapGet("/users/{userId:int}/links", LinksAsync);
        admin.MapPost("/users/{userId:int}/links/discord/unlink", UnlinkDiscordAsync);
    }

    /// <summary>Every fingerprint the account used (most recent first), with the other accounts seen on each.</summary>
    internal static async Task<IResult> DevicesAsync(int userId, IClientHashRepository hashes, IUserRepository users, CancellationToken cancellationToken)
    {
        var names = new Dictionary<int, string>();
        var devices = new List<AccountDevice>();
        foreach (var hash in await hashes.ListAsync(userId, cancellationToken).ConfigureAwait(false))
        {
            var query = ClientHashMatch.For(string.Empty, hash.Adapters, hash.UninstallId, hash.DiskSerial);
            var others = query.IsEmpty ? [] : await hashes.FindOtherUsersAsync(userId, query, 20, cancellationToken).ConfigureAwait(false);
            var people = new List<DeviceUser>();
            foreach (var other in others)
            {
                if (!names.TryGetValue(other, out var name))
                    names[other] = name = (await users.GetByIdAsync(other, cancellationToken).ConfigureAwait(false))?.Username ?? $"#{other}";
                people.Add(new DeviceUser(other, name));
            }
            devices.Add(new AccountDevice(hash.OsuPath, hash.Adapters, hash.UninstallId, hash.DiskSerial, hash.LatestTime, hash.Occurrences, people));
        }
        return Results.Ok(devices);
    }

    /// <summary>Kicks the player's stable session (the client reconnects, and logs in again under the new rules).</summary>
    internal static IResult Disconnect(int userId, DisconnectRequest? request, PlayerRegistry players)
    {
        if (players.GetByUserId(userId) is not { } player)
            return Results.Ok(new { online = false });

        var packets = new BanchoPacketWriter(256);
        packets.WriteStringPacket(ServerPacketId.Notification, string.IsNullOrWhiteSpace(request?.Message) ? "You were disconnected by staff." : request.Message);
        packets.WritePacket(ServerPacketId.Restart, w => w.WriteInt32(0));
        player.Enqueue(packets.ToArray());
        return Results.Ok(new { online = true });
    }

    internal static async Task<IResult> LinksAsync(int userId, IDiscordLinkRepository discord, TomYumDbContext db, CancellationToken cancellationToken)
    {
        var link = await discord.GetByUserIdAsync(userId, cancellationToken).ConfigureAwait(false);
        var lastFm = await db.LastFmLinks.AsNoTracking().FirstOrDefaultAsync(l => l.UserId == userId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new AccountLinks(
            link is null ? null : new AccountDiscordLink(link.DiscordId.ToString(System.Globalization.CultureInfo.InvariantCulture), link.LinkedAt),
            lastFm is null ? null : new AccountLastFmLink(lastFm.Username, lastFm.LinkedAt)));
    }

    internal static async Task<IResult> UnlinkDiscordAsync(int userId, IDiscordLinkRepository discord, CancellationToken cancellationToken)
    {
        var link = await discord.GetByUserIdAsync(userId, cancellationToken).ConfigureAwait(false);
        return link is not null && await discord.UnlinkAsync(link.DiscordId, cancellationToken).ConfigureAwait(false)
            ? Results.Ok(new { unlinked = true })
            : Results.NotFound();
    }
}
