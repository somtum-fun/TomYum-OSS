using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TomYum.Server.Discord;

/// <summary>
/// One-time codes for linking a Discord account to a game account: <c>/link</c> in Discord hands
/// one out, and the player proves they own the game account by sending it to BanchoBot
/// (<c>!link &lt;code&gt;</c>, in-game or in website chat). Codes last <see cref="Lifetime"/>; a
/// Discord account has at most one pending code.
/// </summary>
public sealed class DiscordLinkCodes
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    // No look-alike characters (0/O, 1/I/L).
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int Length = 6;

    private readonly ConcurrentDictionary<string, Pending> _codes = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public DiscordLinkCodes(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Raised after a link is made (the bot gives roles and logs it).</summary>
    public event Func<int, ulong, Task>? Linked;

    /// <summary>A new code for <paramref name="discordId"/>, replacing any it had.</summary>
    public string Create(ulong discordId)
    {
        Prune();
        foreach (var (existing, pending) in _codes)
        {
            if (pending.DiscordId == discordId)
                _codes.TryRemove(existing, out _);
        }

        while (true)
        {
            var code = RandomNumberGenerator.GetString(Alphabet, Length);
            if (_codes.TryAdd(code, new Pending(discordId, _time.GetUtcNow() + Lifetime)))
                return code;
        }
    }

    /// <summary>Takes the code (once): the Discord account it was for, or null when it's unknown or expired.</summary>
    public ulong? Redeem(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (!_codes.TryRemove(code.Trim().ToUpperInvariant(), out var pending))
            return null;
        return pending.Expires > _time.GetUtcNow() ? pending.DiscordId : null;
    }

    /// <summary>Raised after a Discord account unlinks (the bot takes its roles back).</summary>
    public event Func<ulong, Task>? Unlinked;

    /// <summary>Tells listeners a link was removed.</summary>
    public async Task OnUnlinkedAsync(ulong discordId)
    {
        if (Unlinked is { } handlers)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Func<ulong, Task>>())
                await handler(discordId).ConfigureAwait(false);
        }
    }

    /// <summary>Tells listeners a link was made.</summary>
    public async Task OnLinkedAsync(int userId, ulong discordId)
    {
        if (Linked is { } handlers)
        {
            foreach (var handler in handlers.GetInvocationList().Cast<Func<int, ulong, Task>>())
                await handler(userId, discordId).ConfigureAwait(false);
        }
    }

    private void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var (code, pending) in _codes)
        {
            if (pending.Expires <= now)
                _codes.TryRemove(code, out _);
        }
    }

    private sealed record Pending(ulong DiscordId, DateTimeOffset Expires);
}
