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
    private readonly TomYum.Persistence.OsuWeb.OsuWebAccountFlags? _accountFlags;
    private readonly TomYum.Server.Admin.RestrictionNotices? _restrictionNotices;
    private readonly TomYum.Server.Admin.Maintenance? _maintenance;

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
        TomYum.Server.Admin.Maintenance? maintenance = null)
    {
        _maintenance = maintenance;
        _restrictionNotices = restrictionNotices;
        _accountFlags = accountFlags;
        _clientVerification = clientVerificationOptions?.Value.Enabled == true ? clientVerification : null;
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
    public readonly record struct LoginOutcome(byte[] Body, string? Token);

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
            return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.ServerError, _maintenance.Notice), null);
        }

        // Hardware hashes: recorded for every login; an account's first login is refused when the
        // machine belongs to a restricted account (ban evasion), until staff verify it.
        if (_clientHashes is not null && await IsEvadingAsync(user, request.ClientHashes, cancellationToken).ConfigureAwait(false))
            return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.AuthenticationFailed, EvasionNotice), null);

        // A device the player hasn't confirmed (ClientVerificationOptions): the client opens the
        // website's verify page (osu.<domain>/p/verify), where they confirm it after signing in.
        if (_clientVerification is not null && user.Id != BanchoBot.UserId)
        {
            var hashes = request.ClientHashes;
            if (!await _clientVerification.IsVerifiedAsync(user.Id, hashes.OsuPathMd5, hashes.AdaptersMd5, hashes.UninstallMd5, hashes.DiskSignatureMd5, cancellationToken).ConfigureAwait(false))
            {
                HoldDevice(user.Id, hashes.UninstallMd5);
                _logger.LogInformation("Login held: {Username} (id {UserId}) is on a device they haven't verified yet.", user.Username, user.Id);
                await RecordLoginAsync(user.Id, "held", clientAddress, countryIso, request.OsuVersion, cancellationToken).ConfigureAwait(false);
                return new LoginOutcome(LoginResponseBuilder.BuildFailure(LoginFailureReason.VerificationRequired), null);
            }
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

        // osu!'s tournament client (version "…tourney") logs in once per player it shows, with the
        // same account: those windows are extra sessions, not the player moving to another client.
        var tournament = request.OsuVersion.EndsWith("tourney", StringComparison.OrdinalIgnoreCase);
        var player = tournament ? _registry.AddTournamentClient(presence) : _registry.Add(presence);
        player.Build = ClientBuild.Parse(request.OsuVersion);
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
