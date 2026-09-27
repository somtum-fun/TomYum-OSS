using System.Globalization;
using TomYum.Bancho;
using TomYum.Domain.Entities;
using TomYum.Domain.Repositories;
using TomYum.Domain.Services;

namespace TomYum.Server.Bancho;

/// <summary>
/// Turns a raw osu!stable login body into a framed bancho response and a registered
/// <see cref="OnlinePlayer"/>. This is the host-side login decision: parse the credentials,
/// resolve the user, then ask <see cref="LoginResponseBuilder"/> to encode the outcome.
/// </summary>
/// <remarks>
/// Credentials are real: the username is resolved by canonical <see cref="User.SafeName"/>
/// and the client's md5-hex password is verified against the stored bcrypt hash
/// (see <see cref="IPasswordHasher"/>). Still backed by an in-memory repository until
/// <c>TomYum.Persistence</c> lands. Restricted players log in but get the restricted notice and
/// stay invisible to others; silenced players get their remaining silence. TODO: geolocate the
/// client IP for presence (country, lon/lat).
/// </remarks>
public sealed class BanchoLoginHandler
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IUserStatsRepository _userStats;
    private readonly PlayerRegistry _registry;
    private readonly ChannelRegistry _channels;
    private readonly ILogger<BanchoLoginHandler> _logger;

    /// <summary>Creates the handler over the user repository, password hasher, online-player registry, and channel registry.</summary>
    public BanchoLoginHandler(
        IUserRepository users,
        IPasswordHasher passwordHasher,
        IUserStatsRepository userStats,
        PlayerRegistry registry,
        ChannelRegistry channels,
        ILogger<BanchoLoginHandler> logger)
    {
        _users = users;
        _passwordHasher = passwordHasher;
        _userStats = userStats;
        _registry = registry;
        _channels = channels;
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
    /// <param name="body">Raw login request body.</param>
    /// <param name="countryIso">2-letter ISO country code from CF-IPCountry (or empty).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<LoginOutcome> HandleAsync(string body, string countryIso, CancellationToken cancellationToken)
    {
        if (!LoginRequest.TryParse(body, out var request) || request is null)
        {
            _logger.LogWarning("Rejected malformed bancho login body.");
            return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.AuthenticationFailed), null);
        }

        var user = await _users.GetBySafeNameAsync(User.MakeSafeName(request.Username), cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            _logger.LogInformation("Login failed: unknown username {Username}.", request.Username);
            return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.AuthenticationFailed), null);
        }

        // The client sends the md5 hex of the plaintext; verify it against the stored bcrypt.
        if (!_passwordHasher.Verify(request.PasswordMd5, user.PasswordBcrypt))
        {
            _logger.LogInformation("Login failed: bad password for {Username}.", request.Username);
            return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.AuthenticationFailed), null);
        }

        // Resolve the country for this session: prefer the live CF-IPCountry header, fall back
        // to the stored user country. If the stored country is "xx" and we have a real one
        // now, persist it so future logins don't need the header to show the correct flag.
        var resolvedCountry = !string.IsNullOrEmpty(countryIso) ? countryIso.ToLowerInvariant() : user.Country;
        if (user.Country == "xx" && resolvedCountry != "xx")
        {
            await _users.UpdateCountryAsync(user.Id, resolvedCountry, cancellationToken).ConfigureAwait(false);
        }

        var presence = new BanchoPresence
        {
            UserId = user.Id,
            Username = user.Username,
            UtcOffset = request.UtcOffset,
            Privileges = PrivilegeMapper.ToBancho(user.Privileges),
            CountryCode = CountryCodes.ToNumeric(resolvedCountry),
        };

        var stats = await _userStats.GetAsync(user.Id, presence.Mode, cancellationToken).ConfigureAwait(false);
        if (stats is not null)
        {
            var rank = await _userStats.GetGlobalRankAsync(user.Id, presence.Mode, cancellationToken).ConfigureAwait(false);
            presence.RankedScore = stats.RankedScore;
            presence.TotalScore  = stats.TotalScore;
            presence.PlayCount   = stats.PlayCount;
            presence.Accuracy    = stats.Accuracy;
            presence.Performance = (short)Math.Clamp((int)stats.Pp, short.MinValue, short.MaxValue);
            presence.GlobalRank  = rank;
        }

        var player = _registry.Add(presence);
        player.IsRestricted = user.IsRestricted;
        player.SilenceEndUtc = user.SilenceEndUtc;
        var welcome = string.Create(CultureInfo.InvariantCulture, $"Welcome to TomYum, {user.Username}!");

        // Auto-join the player to the auto-join channels (e.g. #osu) so they can chat immediately,
        // and advertise every channel in the login burst. CHANNEL_AUTO_JOIN tells the client to
        // open the tab; CHANNEL_INFO just lists it. Member counts reflect post-join state.
        foreach (var channel in _channels.All)
        {
            if (channel.AutoJoin)
            {
                channel.Join(player);
                player.JoinChannel(channel.Name);
            }
        }

        void WriteChannels(BanchoPacketWriter w)
        {
            foreach (var channel in _channels.All)
            {
                if (channel.AutoJoin)
                {
                    ChatEncoder.WriteChannelAutoJoin(w, channel.Name, channel.Topic, channel.MemberCount);
                }
                else
                {
                    ChatEncoder.WriteChannelInfo(w, channel.Name, channel.Topic, channel.MemberCount);
                }
            }
        }

        // Build the success burst, then append the presence + stats of every other online
        // player so the newcomer's user list is populated immediately. Their own presence/stats
        // are already in the burst (BuildSuccess), so skip self here to avoid a duplicate.
        var writer = new BanchoPacketWriter(1024);
        writer.WriteRaw(LoginResponseBuilder.BuildSuccess(
            presence, welcome, WriteChannels, player.SilenceSecondsLeft(DateTimeOffset.UtcNow)));
        if (player.IsRestricted)
            ModerationPackets.WriteRestricted(writer);

        foreach (var other in _registry.All)
        {
            // Restricted players are invisible to everyone else.
            if (other.UserId == player.UserId || other.IsRestricted)
            {
                continue;
            }

            LoginResponseBuilder.WritePresence(writer, other.Presence);
            LoginResponseBuilder.WriteStats(writer, other.Presence);
        }

        var responseBody = writer.ToArray();

        // Announce the newcomer to everyone already online so they appear in others' user lists.
        var announce = new BanchoPacketWriter(128);
        LoginResponseBuilder.WritePresence(announce, presence);
        LoginResponseBuilder.WriteStats(announce, presence);
        _registry.EnqueueAbout(player, announce.ToArray(), exceptUserId: player.UserId);

        _logger.LogInformation(
            "Login ok: {Username} (id {UserId}) on {Version}; {Online} online.",
            user.Username, user.Id, request.OsuVersion, _registry.Count);

        return new LoginOutcome(responseBody, player.Token);
    }
}
