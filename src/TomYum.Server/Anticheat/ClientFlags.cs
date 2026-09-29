using System.Globalization;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Admin;
using TomYum.Server.Bancho;
using TomYum.Server.OsuWeb;

namespace TomYum.Server.Anticheat;

/// <summary>osu!stable's hidden anti-cheat flags (bancho.py's <c>LastFMFlags</c>).</summary>
[Flags]
public enum LastFmSignals
{
    None = 0,
    RunWithLdFlag = 1 << 14,
    ConsoleOpen = 1 << 15,
    ExtraThreads = 1 << 16,
    HqAssembly = 1 << 17,
    HqFile = 1 << 18,
    RegistryEdits = 1 << 19,
    Sdl2Library = 1 << 20,
    OpenSslLibrary = 1 << 21,
    AqnMenuSample = 1 << 22,
}

/// <summary>
/// What osu!stable reports through <c>/web/lastfm.php</c>: normally a beatmap id (a scrobble, which
/// this server doesn't keep), but <c>b=a&lt;flags&gt;</c> is the client's anti-cheat telling what it
/// found. Like bancho.py: hq!osu running restricts the player; hq!osu's multi-account tool left in the
/// registry gets them a warning and staff an alert (bancho.py rolls a 1 in 32 restriction instead);
/// the other flags are too unreliable to act on and are only logged.
/// </summary>
public static class ClientFlags
{
    /// <summary>The staff log action (and Discord alert) for the multi-account tool's traces.</summary>
    public const string RegistryAction = "user.client_flags";

    /// <summary>Why a player was restricted for hq!osu (shown to staff).</summary>
    public const string HqReason = "hq!osu detected by the osu! client";

    /// <summary>The flags in a <c>b</c> value, or null for a plain beatmap id.</summary>
    public static LastFmSignals? Parse(string? value) =>
        value is ['a', .. var digits] && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var flags)
            ? (LastFmSignals)flags
            : null;

    /// <summary>Acts on the flags <paramref name="user"/>'s client reported.</summary>
    public static async Task HandleAsync(IServiceProvider services, User user, LastFmSignals flags, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(user);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ClientFlags));
        if (flags == LastFmSignals.None)
            return;
        logger.LogInformation("Client flags from {Username} ({UserId}): {Flags}.", user.Username, user.Id, flags);

        if ((flags & (LastFmSignals.HqAssembly | LastFmSignals.HqFile)) != 0)
        {
            await RestrictAsync(services, user, flags, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (flags.HasFlag(LastFmSignals.RegistryEdits))
        {
            if (services.GetRequiredService<PlayerRegistry>().GetByUserId(user.Id) is { } online)
            {
                var writer = new BanchoPacketWriter(256);
                writer.WriteStringPacket(ServerPacketId.Notification,
                    "Hey! Your computer has traces of hq!osu's multi-account tool (relife) in its registry, which osu! detects. "
                    + "Please reinstall relife and turn it off to avoid a restriction.");
                online.Enqueue(writer.ToArray());
            }
            await LogAsync(services, user, RegistryAction, $"{user.Username}'s osu! client found hq!osu's multi-account tool (relife) in the registry ({flags}).", "warned", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RestrictAsync(IServiceProvider services, User user, LastFmSignals flags, CancellationToken cancellationToken)
    {
        // Staff are only reported, like circleguard does.
        if (ModerationCommands.IsStaff(user.Privileges) || user.IsRestricted || services.GetService<OsuWebModeration>() is not { } moderation)
        {
            await LogAsync(services, user, RegistryAction, $"{user.Username}'s osu! client reported hq!osu ({flags}); not restricted automatically.", "reported", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (await moderation.RestrictAsync(user.Id, BanchoBot.UserId, HqReason, cancellationToken).ConfigureAwait(false) != ModerationOutcome.Done)
            return;

        await LogAsync(services, user, "user.restrict", $"Restricted {user.Username}: the osu! client reported hq!osu ({flags}).", "client flags", cancellationToken).ConfigureAwait(false);
        await services.GetRequiredService<UserModerationSync>().RefreshAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (services.GetService<OsuWebSync>() is { } sync)
            await sync.RecalculateCountriesOfAsync([user.Id], cancellationToken).ConfigureAwait(false);
    }

    private static Task LogAsync(IServiceProvider services, User user, string action, string summary, string result, CancellationToken cancellationToken) =>
        services.GetService<IStaffLogRepository>() is { } staffLog
            ? staffLog.AddAsync(new StaffLogEntry
            {
                CreatedAt = DateTime.UtcNow,
                ActorId = BanchoBot.UserId,
                Action = action,
                TargetId = user.Id,
                Summary = summary.Length > 512 ? summary[..512] : summary,
                Result = result,
            }, cancellationToken)
            : Task.CompletedTask;
}
