using System.Globalization;
using TomYum.Bancho;
using TomYum.Domain.Repositories;

namespace TomYum.Server.Bancho;

/// <summary>
/// Turns a raw osu!stable login body into a framed bancho response and a registered
/// <see cref="OnlinePlayer"/>. This is the host-side login decision: parse the credentials,
/// resolve the user, then ask <see cref="LoginResponseBuilder"/> to encode the outcome.
/// </summary>
/// <remarks>
/// First vertical slice (see <c>memories/progress.md</c>): authentication is intentionally
/// minimal — any known username is accepted and the password is not yet verified, because
/// persistence/hashing land in the next slice. The wire handshake, token minting, and the
/// success packet burst are real. TODO: verify <c>password_md5</c> against the stored hash,
/// honour ban/restriction state, and geolocate the client IP for presence.
/// </remarks>
public sealed class BanchoLoginHandler
{
    private readonly IUserRepository _users;
    private readonly PlayerRegistry _registry;
    private readonly ILogger<BanchoLoginHandler> _logger;

    /// <summary>Creates the handler over the user repository and the online-player registry.</summary>
    public BanchoLoginHandler(IUserRepository users, PlayerRegistry registry, ILogger<BanchoLoginHandler> logger)
    {
        _users = users;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>The outcome of a login attempt: the framed response body and the new token (if any).</summary>
    /// <param name="Body">Framed bancho packets to write to the HTTP response body.</param>
    /// <param name="Token">The minted <c>cho-token</c> on success; <c>null</c> on failure.</param>
    public readonly record struct LoginOutcome(byte[] Body, string? Token);

    /// <summary>
    /// Handles a first-contact login (request had no <c>osu-token</c> header). Always returns
    /// a body to send; <see cref="LoginOutcome.Token"/> is non-null only when login succeeded.
    /// </summary>
    public async Task<LoginOutcome> HandleAsync(string body, CancellationToken cancellationToken)
    {
        if (!LoginRequest.TryParse(body, out var request) || request is null)
        {
            _logger.LogWarning("Rejected malformed bancho login body.");
            return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.AuthenticationFailed), null);
        }

        var user = await _users.GetByUsernameAsync(request.Username, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            _logger.LogInformation("Login failed: unknown username {Username}.", request.Username);
            return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.AuthenticationFailed), null);
        }

        // TODO (next slice): verify request.PasswordMd5 against the stored bcrypt/MD5 hash
        // and reject with AuthenticationFailed on mismatch.

        var presence = new BanchoPresence
        {
            UserId = user.Id,
            Username = user.Username,
            UtcOffset = request.UtcOffset,
            Privileges = BanchoPrivileges.Player,
        };

        var player = _registry.Add(presence);
        var welcome = string.Create(CultureInfo.InvariantCulture, $"Welcome to TomYum, {user.Username}!");
        var responseBody = LoginResponseBuilder.BuildSuccess(presence, welcome);

        _logger.LogInformation(
            "Login ok: {Username} (id {UserId}) on {Version}; {Online} online.",
            user.Username, user.Id, request.OsuVersion, _registry.Count);

        return new LoginOutcome(responseBody, player.Token);
    }
}
