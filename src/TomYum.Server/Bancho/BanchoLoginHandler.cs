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
/// stay invisible to others; silenced players get their remaining silence. The country comes from
/// the endpoint (Cloudflare's header, else <see cref="IpCountry"/>); presence has no coordinates.
/// </remarks>
public sealed class BanchoLoginHandler
{
    /// <summary>
    /// Devices whose login was held for verification (player id, uninstall md5), and when: the next
    /// login from one, after the player confirmed it on the website, is welcomed as verified.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int UserId, string Device), DateTimeOffset> HeldDevices = new();

    private static void HoldDevice(int userId, string device)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var held in HeldDevices)
        {
            if (now - held.Value > TimeSpan.FromDays(1))
                HeldDevices.TryRemove(held.Key, out _);
        }
        HeldDevices[(userId, device)] = now;
    }

    private readonly IUserRepository _users;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IUserStatsRepository _userStats;
    private readonly PlayerRegistry _registry;
    private readonly ChannelRegistry _channels;
    private readonly ILogger<BanchoLoginHandler> _logger;
    private readonly TomYum.Plugins.Abstractions.IEventBus? _events;
    private readonly IClientHashRepository? _clientHashes;
    private readonly IStaffLogRepository? _staffLog;
    private readonly PresenceStats? _presenceStats;
    private readonly IFriendRepository? _friends;
    private readonly TomYum.Persistence.OsuWeb.OsuWebPrivateMessages? _privateMessages;
    private readonly TomYum.Persistence.OsuWeb.OsuWebClientVerification? _clientVerification;
    private readonly ClientVerificationOptions _verificationOptions;
    private readonly TomYum.Persistence.OsuWeb.OsuWebAccountFlags? _accountFlags;
    private readonly TomYum.Server.Admin.RestrictionNotices? _restrictionNotices;
    private readonly TomYum.Server.Admin.Maintenance? _maintenance;
    private readonly IServiceProvider? _services;

    /// <summary>At most this many unread private messages are shown at login (the newest).</summary>
    public const int UnreadLimit = 50;

    /// <summary>What a locked account is told (staff lock it from the admin panel).</summary>
    public const string LockedNotice = "This account is locked. Please contact staff.";

    /// <summary>What a new account sharing a machine with a restricted one is told.</summary>
    public const string EvasionNotice = "Please contact staff directly to create an account.";

    /// <summary>Shown to a deleted account (the admin panel's Delete).</summary>
    public const string DeletedNotice = "This account has been deleted.";

    /// <summary>Shown to a bot account trying the game: bots chat over IRC (port 6667) instead.</summary>
    public const string BotNotice = "Bot accounts can't log in to the game. Connect over IRC instead.";

    /// <summary>Shown to a second client logging in while the account's session is still active (bancho.py's wording).</summary>
    public const string AlreadyOnlineNotice = "User already logged in.";

    /// <summary>A session heard from this recently is active, and a second login is refused (bancho.py's 10 seconds).</summary>
    public static readonly TimeSpan ActiveSessionWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// What a held-for-verification login is told, with the website's verify page on
    /// <paramref name="domain"/> (<c>Server:Domain</c>): today's clients open <c>/p/verify</c> on their own
    /// when they see the <see cref="LoginFailureReason.VerificationRequired"/> code, but the page also
    /// works without the client's details (it confirms the account's newest waiting device). Shown as a
    /// bancho notification, once while a device waits (see <see cref="NoticeAgain"/>).
    /// </summary>
    /// <param name="domain">The server's public domain.</param>
    public static string VerificationNotice(string domain) =>
        "This is a new device, so it needs confirming first.\n"
      + $"Sign in on https://osu.{domain}/p/verify and confirm it there, then log in to the game again.";

    /// <summary>
    /// What an old client that can't verify devices is told when its device needs verifying
    /// (<see cref="OldClientPolicy.Refuse"/>): a client that can is at <paramref name="downloadsUrl"/>. When the
    /// client sent its device's details (<paramref name="hasDevice"/>), the website can confirm it too.
    /// </summary>
    /// <param name="domain">The server's public domain.</param>
    /// <param name="downloadsUrl">Where clients that can verify are.</param>
    /// <param name="hasDevice">Whether the device was recorded (the client sent its hardware hashes).</param>
    public static string OldClientNotice(string domain, string downloadsUrl, bool hasDevice) =>
        hasDevice
            ? "This is a new device, and this version of osu! can't confirm it by itself.\n"
              + $"Sign in on https://osu.{domain}/p/verify to confirm it, then log in again,\n"
              + $"or get a client that can at {downloadsUrl}"
            : "This version of osu! can't confirm a new device.\n"
              + $"Please get a client that can at {downloadsUrl}, then log in again.";

    /// <summary>A device still waiting after this long gets the verification notice again (it's not repeated on every retry).</summary>
    public static readonly TimeSpan NoticeAgain = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The staff log action for an account that got in earlier and now logs in from the machine of a
    /// restricted account (it was restricted later, or the account was verified by hand); staff are
    /// alerted on Discord once per pair.
    /// </summary>
    public const string SharedMachineAction = "user.shared_machine";

    /// <summary>Creates the handler over the user repository, password hasher, online-player registry, and channel registry.</summary>
    public BanchoLoginHandler(
        IUserRepository users,
        IPasswordHasher passwordHasher,
        IUserStatsRepository userStats,
        PlayerRegistry registry,
        ChannelRegistry channels,
        ILogger<BanchoLoginHandler> logger,
        TomYum.Plugins.Abstractions.IEventBus? events = null,
        IClientHashRepository? clientHashes = null,
        IStaffLogRepository? staffLog = null,
        PresenceStats? presenceStats = null,
        IFriendRepository? friends = null,
        TomYum.Persistence.OsuWeb.OsuWebPrivateMessages? privateMessages = null,
        Microsoft.Extensions.Options.IOptions<ClientVerificationOptions>? clientVerificationOptions = null,
        TomYum.Persistence.OsuWeb.OsuWebClientVerification? clientVerification = null,
        TomYum.Persistence.OsuWeb.OsuWebAccountFlags? accountFlags = null,
        TomYum.Server.Admin.RestrictionNotices? restrictionNotices = null,
        TomYum.Server.Admin.Maintenance? maintenance = null,
        IServiceProvider? services = null)
    {
        _services = services;
        _maintenance = maintenance;
        _restrictionNotices = restrictionNotices;
        _accountFlags = accountFlags;
        _clientVerification = clientVerificationOptions?.Value.Enabled == true ? clientVerification : null;
        _verificationOptions = clientVerificationOptions?.Value ?? new ClientVerificationOptions();
        _friends = friends;
        _privateMessages = privateMessages;
        _presenceStats = presenceStats;
        _users = users;
        _passwordHasher = passwordHasher;
        _userStats = userStats;
        _registry = registry;
        _channels = channels;
        _logger = logger;
        _events = events;
        _clientHashes = clientHashes;
        _staffLog = staffLog;
    }

    /// <summary>The outcome of a login attempt: the framed response body and the new token (if any).</summary>
    /// <param name="Body">Framed bancho packets to write to the HTTP response body.</param>
    /// <param name="Token">The minted <c>cho-token</c> on success; <c>null</c> on failure.</param>
    /// <param name="Held">Refused for now, not for good: maintenance, or a device to verify first.</param>
    public readonly record struct LoginOutcome(byte[] Body, string? Token, bool Held = false);

    /// <summary>
    /// Handles a first-contact login (request had no <c>osu-token</c> header). Always returns
    /// a body to send; <see cref="LoginOutcome.Token"/> is non-null only when login succeeded.
    /// </summary>
    /// <param name="body">Raw login request body.</param>
    /// <param name="countryIso">2-letter ISO country code (Cloudflare's, else GeoIP's), or empty.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="clientAddress">The player's IP address, for their login history (or null).</param>
    public async Task<LoginOutcome> HandleAsync(string body, string countryIso, CancellationToken cancellationToken, string? clientAddress = null)
    {
        var outcome = await HandleCoreAsync(body, countryIso, clientAddress, cancellationToken).ConfigureAwait(false);

        // Plugins hear about refused logins too (a successful one raises PlayerLoginEvent).
        if (outcome.Token is null && _events is not null)
        {
            var username = LoginRequest.TryParse(body, out var request) && request is not null ? request.Username : string.Empty;
            await _events.PublishAsync(new TomYum.Plugins.Abstractions.PlayerLoginRefusedEvent(username, outcome.Held), cancellationToken).ConfigureAwait(false);
        }
        return outcome;
    }

    private async Task<LoginOutcome> HandleCoreAsync(string body, string countryIso, string? clientAddress, CancellationToken cancellationToken)
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
            await RecordLoginAsync(user.Id, "bad_password", clientAddress, countryIso, request.OsuVersion, cancellationToken).ConfigureAwait(false);
            return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.AuthenticationFailed), null);
        }

        // Deleted from the admin panel: no logins (it isn't a restriction, which still lets them play).
        if (user.IsDeleted)
        {
            _logger.LogInformation("Login refused: {Username} (id {UserId}) is deleted.", user.Username, user.Id);
            await RecordLoginAsync(user.Id, "deleted", clientAddress, countryIso, request.OsuVersion, cancellationToken).ConfigureAwait(false);
            return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.AuthenticationFailed, DeletedNotice), null);
        }

        // Bot accounts (the admin panel's bots) chat over IRC; the game is for players. BanchoBot
        // never logs in here.
        if (user.IsBot)
        {
            _logger.LogInformation("Login refused: {Username} (id {UserId}) is a bot account.", user.Username, user.Id);
            await RecordLoginAsync(user.Id, "bot", clientAddress, countryIso, request.OsuVersion, cancellationToken).ConfigureAwait(false);
            return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.AuthenticationFailed, BotNotice), null);
        }

        // Locked by staff (the admin panel): no logins anywhere until unlocked.
        if (_accountFlags is not null && await _accountFlags.IsLockedAsync(user.Id, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("Login refused: {Username} (id {UserId}) is locked.", user.Username, user.Id);
            await RecordLoginAsync(user.Id, "locked", clientAddress, countryIso, request.OsuVersion, cancellationToken).ConfigureAwait(false);
            return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.AuthenticationFailed, LockedNotice), null);
        }

        // Maintenance (the admin panel): only staff get in. "Server error" makes stable keep
        // reconnecting, so players are back on their own once it's over.
        if (_maintenance?.IsLocked == true && !ModerationCommands.IsStaff(user.Privileges))
        {
            _logger.LogInformation("Login held: {Username} (id {UserId}); maintenance.", user.Username, user.Id);
            return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.ServerError, _maintenance.Notice), null, Held: true);
        }

        // Hardware hashes: recorded for every login; an account's first login is refused when the
        // machine belongs to a restricted account (ban evasion), until staff verify it.
        if (_clientHashes is not null && await IsEvadingAsync(user, request.ClientHashes, cancellationToken).ConfigureAwait(false))
            return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.AuthenticationFailed, EvasionNotice), null);

        // A device the player hasn't confirmed (ClientVerificationOptions): the client opens the
        // website's verify page (osu.<domain>/p/verify), where they confirm it after signing in.
        // Builds that can't be asked (before b20151228, not patched for it) follow OldClients.
        var build = ClientBuild.Parse(request.OsuVersion) with { HandlesVerification = request.HandlesVerification };
        if (_clientVerification is not null && user.Id != BanchoBot.UserId)
        {
            var hashes = request.ClientHashes;
            var verified = await _clientVerification.IsVerifiedAsync(user.Id, hashes.OsuPathMd5, hashes.AdaptersMd5, hashes.UninstallMd5, hashes.DiskSignatureMd5, cancellationToken).ConfigureAwait(false);
            var hasDevice = TomYum.Persistence.OsuWeb.OsuWebClientVerification.IsDevice(hashes.OsuPathMd5, hashes.UninstallMd5, hashes.DiskSignatureMd5);
            if (!hasDevice)
            {
                // No usable hardware hashes (builds before b20120812) can't be checked or confirmed. Under
                // Refuse an old build like that is a new device for any account that has one already (an
                // account's first is trusted, as any first device is); otherwise it gets in, as before.
                // Today's clients always send them: one that doesn't (a client made to skip the check)
                // is a new device too, whatever OldClients says.
                if ((!build.IsLegacy || _verificationOptions.OldClients == OldClientPolicy.Refuse)
                    && await _clientVerification.HasDevicesAsync(user.Id, cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("Login refused: {Username} (id {UserId}) is on a build that sends no device ({Version}).", user.Username, user.Id, request.OsuVersion);
                    await RecordLoginAsync(user.Id, "old_client", clientAddress, countryIso, request.OsuVersion, cancellationToken).ConfigureAwait(false);
                    return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.AuthenticationFailed, OldClientNotice(Domain, DownloadsUrl, hasDevice: false)), null);
                }
                verified = true;
            }

            if (!verified && build.CanVerifyDevices)
            {
                var noticeDue = !HeldDevices.TryGetValue((user.Id, hashes.UninstallMd5), out var heldSince) || DateTimeOffset.UtcNow - heldSince > NoticeAgain;
                HoldDevice(user.Id, hashes.UninstallMd5);
                // A patched old client retries the whole login every 15 s while it waits: log and record it once.
                if (noticeDue)
                {
                    _logger.LogInformation("Login held: {Username} (id {UserId}) is on a device they haven't verified yet.", user.Username, user.Id);
                    await RecordLoginAsync(user.Id, "held", clientAddress, countryIso, request.OsuVersion, cancellationToken).ConfigureAwait(false);
                }
                var held = noticeDue
                    ? LoginResponseBuilder.BuildFailure(LoginFailureReason.VerificationRequired, VerificationNotice(Domain))
                    : LoginResponseBuilder.BuildFailure(LoginFailureReason.VerificationRequired);
                return new LoginOutcome(held, null, Held: true);
            }

            if (!verified && _verificationOptions.OldClients == OldClientPolicy.Refuse)
            {
                _logger.LogInformation("Login refused: {Username} (id {UserId}) is on a device they haven't verified, with a build that can't verify it ({Version}).", user.Username, user.Id, request.OsuVersion);
                await RecordLoginAsync(user.Id, "old_client", clientAddress, countryIso, request.OsuVersion, cancellationToken).ConfigureAwait(false);
                return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.AuthenticationFailed, OldClientNotice(Domain, DownloadsUrl, hasDevice: true)), null);
            }

            if (!verified)
                _logger.LogInformation("Login: {Username} (id {UserId}) is on a device they haven't verified, with a build that can't verify it ({Version}); let in (ClientVerification:OldClients is Unverified).", user.Username, user.Id, request.OsuVersion);
        }

        // osu!'s tournament client (version "…tourney") logs in once per player it shows, with the
        // same account: those windows are extra sessions, not the player moving to another client.
        var tournament = request.OsuVersion.EndsWith("tourney", StringComparison.OrdinalIgnoreCase);

        // One game client per account, as bancho.py does: while the session already online is still
        // talking (heard from in the last 10 seconds) this login is refused; a quiet one (crashed,
        // closed without logging out) is logged out to make room. IRC sessions give way as before.
        if (!tournament && _registry.GetByUserId(user.Id) is { IsIrc: false } online && user.Id != BanchoBot.UserId)
        {
            if (DateTimeOffset.UtcNow - online.LastActivityUtc < ActiveSessionWindow)
            {
                _logger.LogInformation("Login refused: {Username} (id {UserId}) is already online.", user.Username, user.Id);
                await RecordLoginAsync(user.Id, "already_online", clientAddress, countryIso, request.OsuVersion, cancellationToken).ConfigureAwait(false);
                return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.AuthenticationFailed, AlreadyOnlineNotice), null);
            }

            _logger.LogInformation("Login: {Username} (id {UserId})'s previous session was quiet; logging it out.", user.Username, user.Id);
            await LogOutAsync(online).ConfigureAwait(false);
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

        await (_presenceStats ?? new PresenceStats(_userStats)).FillAsync(presence, user.Id, presence.Mode, cancellationToken: cancellationToken).ConfigureAwait(false);

        var player = tournament ? _registry.AddTournamentClient(presence) : _registry.Add(presence);
        player.Build = build;
        player.IsRestricted = user.IsRestricted;
        player.SilenceEndUtc = user.SilenceEndUtc;
        IReadOnlyList<int> friendIds = _friends is null ? [] : await _friends.GetFriendIdsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        player.Friends = [.. friendIds];

        // Plugins (welcome messages, per-login state). A plugin fault is logged, never breaks login.
        if (_events is not null && !tournament)
            await _events.PublishAsync(new TomYum.Plugins.Abstractions.PlayerLoginEvent(user.Id, user.Username, resolvedCountry ?? string.Empty), cancellationToken).ConfigureAwait(false);
        var welcome = string.Create(CultureInfo.InvariantCulture, $"Welcome to TomYum, {user.Username}!");
        if (HeldDevices.TryRemove((user.Id, request.ClientHashes.UninstallMd5), out _))
            welcome = "This device is now verified. " + welcome;

        // Auto-join the player to the auto-join channels (e.g. #osu) so they can chat immediately,
        // and advertise every channel in the login burst. CHANNEL_AUTO_JOIN tells the client to
        // open the tab; CHANNEL_INFO just lists it. Member counts reflect post-join state.
        // (Tournament windows join only their match's chat: channels know members by account.)
        if (!tournament)
        {
            foreach (var channel in _channels.All)
            {
                if (channel.AutoJoin)
                {
                    channel.Join(player);
                    player.JoinChannel(channel.Name);
                }
            }
        }

        void WriteChannels(BanchoPacketWriter w)
        {
            foreach (var channel in _channels.All)
            {
                if (channel.AutoJoin)
                {
                    ChatEncoder.WriteChannelAutoJoin(w, channel.Name, channel.Topic, channel.MemberCount);
                    // Today's client answers "auto-join" by joining (and gets JOIN_SUCCESS); an old build
                    // (docs/old-clients.md) shows the tab but never asks, so its chat stays on
                    // "Connecting...". It's already in the channel (above): tell it so.
                    if (player.Build.IsLegacy && player.Channels.Contains(channel.Name))
                        ChatEncoder.WriteChannelJoinSuccess(w, channel.Name);
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
            presence, welcome, WriteChannels, player.SilenceSecondsLeft(DateTimeOffset.UtcNow), friendIds));
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

        if (_privateMessages is not null && !tournament)
            await WriteUnreadAsync(writer, _privateMessages, user, cancellationToken).ConfigureAwait(false);

        var responseBody = writer.ToArray();

        // Announce the newcomer to everyone already online so they appear in others' user lists.
        if (!tournament)
        {
            var announce = new BanchoPacketWriter(128);
            LoginResponseBuilder.WritePresence(announce, presence);
            LoginResponseBuilder.WriteStats(announce, presence);
            _registry.EnqueueAbout(player, announce.ToArray(), exceptUserId: player.UserId, isUpdate: false);
        }

        _logger.LogInformation(
            "Login ok: {Username} (id {UserId}) on {Version}; {Online} online.",
            user.Username, user.Id, request.OsuVersion, _registry.Count);
        await RecordLoginAsync(user.Id, "ok", clientAddress, countryIso, request.OsuVersion, cancellationToken).ConfigureAwait(false);

        // A restriction (or its end) they haven't been told about, e.g. it happened while they were
        // away: BanchoBot's PM arrives with their first update.
        if (_restrictionNotices is not null && !tournament)
            await _restrictionNotices.NotifyAsync(user.Id, user.Username, user.IsRestricted, cancellationToken).ConfigureAwait(false);

        return new LoginOutcome(responseBody, player.Token);
    }

    /// <summary>The login in the account's history (the admin panel); a failure to record never fails the login.</summary>
    /// <summary>
    /// Logs <paramref name="player"/>'s session out as its own LOGOUT packet would (match, spectators,
    /// channels, everyone's user list). Without the packet handlers (tests), the new session just
    /// replaces it in the registry.
    /// </summary>
    private async Task LogOutAsync(OnlinePlayer player)
    {
        if (_services?.GetService<Packets.BanchoPacketDispatcher>() is not { } dispatcher)
            return;
        try
        {
            var logout = new BanchoPacketWriter(16).WritePacket((ServerPacketId)ClientPacketId.Logout, w => w.WriteInt32(0)).ToArray();
            await dispatcher.DispatchAsync(player, _registry, _channels,
                _services.GetRequiredService<MatchRegistry>(), _services, logout).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // the new login replaces the session in the registry either way
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Couldn't log out {UserId}'s previous session.", player.UserId);
        }
    }

    /// <summary>The server's public domain (<c>Server:Domain</c>), for the links in notices.</summary>
    private string Domain =>
        _services?.GetService(typeof(IConfiguration)) is IConfiguration configuration ? BanchoHosts.ResolveDomain(configuration) : BanchoHosts.DefaultDomain;

    /// <summary>Where clients that can verify devices are (<see cref="ClientVerificationOptions.DownloadsUrl"/>, else the website's download page).</summary>
    private string DownloadsUrl =>
        string.IsNullOrWhiteSpace(_verificationOptions.DownloadsUrl) ? $"https://osu.{Domain}/home/download" : _verificationOptions.DownloadsUrl.Trim();

    private async Task RecordLoginAsync(int userId, string result, string? address, string country, string version, CancellationToken cancellationToken)
    {
        if (_accountFlags is null)
            return;
        try
        {
            await _accountFlags.RecordLoginAsync(userId, "stable", result, address, country, version, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // the history is best effort: the login goes on
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Couldn't record {UserId}'s login in their history.", userId);
        }
    }

    /// <summary>
    /// Records the login's hashes and looks for restricted accounts on the same machine (staff and
    /// BanchoBot are never checked). An unverified account is refused when there's one, else it's
    /// verified (staff can verify one by hand with <c>!verify</c>). A verified account gets in, but
    /// staff are told once per restricted account it shares the machine with.
    /// </summary>
    private async Task<bool> IsEvadingAsync(User user, ClientHashes hashes, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await _clientHashes!.RecordAsync(new ClientHash
        {
            UserId = user.Id,
            OsuPath = ClientHashMatch.Normalize(hashes.OsuPathMd5),
            Adapters = ClientHashMatch.Normalize(hashes.AdaptersMd5),
            UninstallId = ClientHashMatch.Normalize(hashes.UninstallMd5),
            DiskSerial = ClientHashMatch.Normalize(hashes.DiskSignatureMd5),
            LatestTime = now,
        }, cancellationToken).ConfigureAwait(false);

        if (user.Id == BanchoBot.UserId || ModerationCommands.IsStaff(user.Privileges))
            return false;

        var verified = await _clientHashes.IsVerifiedAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var query = ClientHashMatch.For(hashes.AdaptersStr, hashes.AdaptersMd5, hashes.UninstallMd5, hashes.DiskSignatureMd5);
        var others = query.IsEmpty
            ? []
            : await _clientHashes.FindOtherUsersAsync(user.Id, query, cancellationToken: cancellationToken).ConfigureAwait(false);
        foreach (var otherId in others)
        {
            var other = await _users.GetByIdAsync(otherId, cancellationToken).ConfigureAwait(false);
            if (other is not { IsRestricted: true })
                continue;
            if (verified)
            {
                if (!user.IsRestricted)
                    await AlertSharedMachineAsync(user, other, now, cancellationToken).ConfigureAwait(false);
                continue;
            }

            _logger.LogWarning("Login refused: {Username} (id {UserId}) shares a machine with restricted {Other} (id {OtherId}).",
                user.Username, user.Id, other.Username, other.Id);
            if (_staffLog is not null)
            {
                await _staffLog.AddAsync(new StaffLogEntry
                {
                    CreatedAt = now,
                    Action = "user.evasion_blocked",
                    TargetId = user.Id,
                    Summary = $"{user.Username}'s first login was refused: same machine as restricted {other.Username} (id {other.Id}).",
                }, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }

        if (!verified)
            await _clientHashes.VerifyAsync(user.Id, 0, now, cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>Tells staff (once per pair) that a verified account shares a machine with a restricted one.</summary>
    private async Task AlertSharedMachineAsync(User user, User restricted, DateTime now, CancellationToken cancellationToken)
    {
        if (_staffLog is null)
            return;
        var pair = restricted.Id.ToString(CultureInfo.InvariantCulture);
        var earlier = await _staffLog.ListAsync(new StaffLogQuery(Action: SharedMachineAction, TargetId: user.Id, Limit: 200), cancellationToken).ConfigureAwait(false);
        if (earlier.Any(e => e.Result == pair))
            return;

        _logger.LogWarning("{Username} (id {UserId}) logged in from the same machine as restricted {Other} (id {OtherId}).",
            user.Username, user.Id, restricted.Username, restricted.Id);
        await _staffLog.AddAsync(new StaffLogEntry
        {
            CreatedAt = now,
            Action = SharedMachineAction,
            TargetId = user.Id,
            Summary = $"{user.Username} (id {user.Id.ToString(CultureInfo.InvariantCulture)}) logged in from the same machine as restricted {restricted.Username} (id {pair}). They got in before that account was restricted, or were verified by hand; nothing was done to them.",
            Result = pair,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Private messages that came while the player was away (on the website, or in-game before they
    /// logged in), as osu! shows missed messages: in their conversations, with a word from BanchoBot.
    /// They stay unread until the conversation is opened (<c>osu-markasread.php</c>) or the player logs out.
    /// </summary>
    private async Task WriteUnreadAsync(BanchoPacketWriter writer, TomYum.Persistence.OsuWeb.OsuWebPrivateMessages messages, User user, CancellationToken cancellationToken)
    {
        IReadOnlyList<TomYum.Persistence.OsuWeb.UnreadMessage> unread;
        try
        {
            unread = await messages.UnreadAsync(user.Id, UnreadLimit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unread messages for {UserId} couldn't be read.", user.Id);
            return;
        }
        if (unread.Count == 0)
            return;

        var senders = unread.Select(m => m.SenderName).Distinct(StringComparer.Ordinal).ToList();
        var note = senders.Count == 1
            ? $"You have unread messages from {senders[0]}."
            : $"You have unread messages from {string.Join(", ", senders.Take(5))}{(senders.Count > 5 ? " and others" : string.Empty)}.";
        ChatEncoder.WriteMessage(writer, new BanchoMessage(BanchoBot.Name, note, user.Username, BanchoBot.UserId));
        foreach (var message in unread)
        {
            var text = message.IsAction ? $"\u0001ACTION {message.Content}\u0001" : message.Content;
            ChatEncoder.WriteMessage(writer, new BanchoMessage(message.SenderName, text, user.Username, message.SenderId));
        }
    }
}
