using TomYum.Domain.Entities;

namespace TomYum.Server.Bancho;

/// <summary>
/// Whether an account whose credentials checked out may use them: not deleted, not locked by staff (the
/// admin panel), and not a bot where bots don't belong. Every credential path asks this after the
/// password (the game's login has its own notices for the same rules, in <see cref="BanchoLoginHandler"/>).
/// </summary>
public static class AccountAccess
{
    /// <summary>Why <paramref name="user"/> can't sign in, as the player is told; null when they can.</summary>
    /// <param name="user">The account whose password checked out.</param>
    /// <param name="services">Where the staff lock is read from (a request's or a scope's services).</param>
    /// <param name="allowBots">True where bot accounts belong (IRC).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<string?> RefusalAsync(User user, IServiceProvider services, bool allowBots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(services);
        if (user.IsDeleted)
            return BanchoLoginHandler.DeletedNotice;
        if (user.IsBot && !allowBots)
            return BanchoLoginHandler.BotNotice;
        if (services.GetService<TomYum.Persistence.OsuWeb.OsuWebAccountFlags>() is { } flags
            && await flags.IsLockedAsync(user.Id, cancellationToken).ConfigureAwait(false))
            return BanchoLoginHandler.LockedNotice;
        return null;
    }

    /// <summary>Whether <paramref name="user"/> may sign in with the credentials that just checked out.</summary>
    /// <param name="user">The account whose password checked out.</param>
    /// <param name="services">Where the staff lock is read from (a request's or a scope's services).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="allowBots">True where bot accounts belong (IRC).</param>
    public static async Task<bool> CanAuthenticateAsync(User user, IServiceProvider services, CancellationToken cancellationToken, bool allowBots = false) =>
        await RefusalAsync(user, services, allowBots, cancellationToken).ConfigureAwait(false) is null;
}
