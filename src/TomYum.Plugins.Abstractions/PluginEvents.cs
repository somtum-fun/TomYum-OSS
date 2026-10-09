namespace TomYum.Plugins.Abstractions;

/// <summary>
/// Base class for <see cref="IVetoableEvent"/> implementations. A handler calls <see cref="Veto"/>
/// to reject the in-flight operation; the host inspects <see cref="IsVetoed"/> after dispatch and
/// aborts when set. The first veto wins — its reason is kept and later vetoes are ignored.
/// </summary>
public abstract class VetoableEvent : IVetoableEvent
{
    /// <inheritdoc/>
    public bool IsVetoed { get; private set; }

    /// <inheritdoc/>
    public string? VetoReason { get; private set; }

    /// <inheritdoc/>
    public void Veto(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (IsVetoed)
            return;

        IsVetoed = true;
        VetoReason = reason;
    }
}

/// <summary>
/// Raised after a player completes a successful login and their session is registered — the hook
/// for login-time plugin logic (welcome messages, per-login state seeding).
/// </summary>
/// <param name="UserId">The player's user id.</param>
/// <param name="Username">The player's display name.</param>
/// <param name="CountryIso">Two-letter country code resolved for the session (may be empty).</param>
public sealed record PlayerLoginEvent(long UserId, string Username, string CountryIso) : IPluginEvent;

/// <summary>
/// Raised when an osu!stable login is refused: unknown user, wrong password, a locked, deleted or bot
/// account, and so on. <see cref="Held"/> is true when it's refused only for now (maintenance, or a
/// new device the player has to verify first). A successful login raises <see cref="PlayerLoginEvent"/>.
/// </summary>
/// <param name="Username">The username the client sent (unverified; empty when the request was unreadable).</param>
/// <param name="Held">Whether the login was held rather than refused for good.</param>
public sealed record PlayerLoginRefusedEvent(string Username, bool Held) : IPluginEvent;

/// <summary>
/// Vetoable: raised just before an osu! login succeeds, after built-in gates (password, device
/// verification, evasion check, maintenance) have passed. Carries the client-reported fields a
/// plugin needs to run its own gate (osu!auth hash validation, build whitelist, nonce-signed
/// challenge). A veto turns the login into an authentication failure with the veto reason shown
/// to the player; keep reasons generic ("please update your game client") so an attacker reading the
/// client text does not learn which check failed. The host skips this dispatch entirely when
/// <see cref="IChallengeService.DevMode"/> is on.
/// </summary>
public sealed class ClientLoginAttemptedEvent : VetoableEvent
{
    /// <summary>Creates the pre-login event.</summary>
    public ClientLoginAttemptedEvent(
        long userId,
        string username,
        string osuVersion,
        string osuPathMd5,
        string adaptersMd5,
        string adaptersRaw,
        string uninstallMd5,
        string diskSignatureMd5,
        string countryIso,
        string? clientAddress)
    {
        UserId = userId;
        Username = username;
        OsuVersion = osuVersion;
        OsuPathMd5 = osuPathMd5;
        AdaptersMd5 = adaptersMd5;
        AdaptersRaw = adaptersRaw;
        UninstallMd5 = uninstallMd5;
        DiskSignatureMd5 = diskSignatureMd5;
        CountryIso = countryIso;
        ClientAddress = clientAddress;
    }

    /// <summary>The authenticated user's id.</summary>
    public long UserId { get; }

    /// <summary>Display name.</summary>
    public string Username { get; }

    /// <summary>Client-reported osu! version string (e.g. <c>b20231030.1</c> on stable).</summary>
    public string OsuVersion { get; }

    /// <summary>MD5 of the osu! install path (stable's uninstall-id sibling); empty on lazer.</summary>
    public string OsuPathMd5 { get; }

    /// <summary>MD5 of the enabled network adapters' MACs (stable); empty on lazer.</summary>
    public string AdaptersMd5 { get; }

    /// <summary>Raw adapters string (pre-hash) — osu!auth validators need the list before it was hashed.</summary>
    public string AdaptersRaw { get; }

    /// <summary>MD5 of the install-registry uninstall id (stable); empty on lazer.</summary>
    public string UninstallMd5 { get; }

    /// <summary>MD5 of the disk serial (stable); empty on lazer.</summary>
    public string DiskSignatureMd5 { get; }

    /// <summary>Two-letter country code resolved for this login (CF-IPCountry when present, else GeoIP, else <c>xx</c>).</summary>
    public string CountryIso { get; }

    /// <summary>The player's IP for this login, or null when none is available.</summary>
    public string? ClientAddress { get; }
}

/// <summary>
/// Vetoable: raised when the osu!stable in-game registration endpoint (<c>POST /users</c>) is
/// about to validate or create an account. Carries the submitted username / email / IP /
/// country. A veto turns the response into a <c>form_error</c> on the username field with the
/// veto reason. The host skips this dispatch when <see cref="IChallengeService.DevMode"/> is on.
/// </summary>
public sealed class AccountRegistrationAttemptedEvent : VetoableEvent
{
    /// <summary>Creates the pre-registration event.</summary>
    public AccountRegistrationAttemptedEvent(
        string username,
        string email,
        string countryIso,
        string? clientAddress,
        bool isCreate)
    {
        Username = username;
        Email = email;
        CountryIso = countryIso;
        ClientAddress = clientAddress;
        IsCreate = isCreate;
    }

    /// <summary>Submitted username (unverified).</summary>
    public string Username { get; }

    /// <summary>Submitted email (unverified).</summary>
    public string Email { get; }

    /// <summary>Two-letter country code (CF-IPCountry, else GeoIP, else <c>xx</c>).</summary>
    public string CountryIso { get; }

    /// <summary>Submitter IP, or null when none is available.</summary>
    public string? ClientAddress { get; }

    /// <summary>True on the final submit (<c>check=0</c>); false on per-keystroke validation.</summary>
    public bool IsCreate { get; }
}

/// <summary>
/// Vetoable: raised when a client anti-cheat handshake flag has arrived AND
/// <see cref="IChallengeService.Verify"/> has accepted it. Plugins may still veto on top of
/// the signature check (build id not on allowlist, measurement not recognised). A veto drops
/// the session with the veto reason.
/// </summary>
public sealed class ClientFlagReceivedEvent : VetoableEvent
{
    /// <summary>Creates the post-verify flag event.</summary>
    public ClientFlagReceivedEvent(long userId, string sessionKey, string buildId, ReadOnlyMemory<byte> processMeasurement)
    {
        UserId = userId;
        SessionKey = sessionKey;
        BuildId = buildId;
        ProcessMeasurement = processMeasurement;
    }

    /// <summary>The player's user id.</summary>
    public long UserId { get; }

    /// <summary>Session key the challenge was issued against.</summary>
    public string SessionKey { get; }

    /// <summary>Build id the client reported.</summary>
    public string BuildId { get; }

    /// <summary>Measurement the client supplied; empty when the plugin issuing the challenge did not require one.</summary>
    public ReadOnlyMemory<byte> ProcessMeasurement { get; }
}

/// <summary>Raised when a player sends a chat message, before it is delivered to recipients.</summary>
/// <param name="SenderUserId">Id of the sending player.</param>
/// <param name="SenderUsername">Display name of the sending player.</param>
/// <param name="Channel">Target channel (e.g. <c>#osu</c>), or <c>null</c> for a private message.</param>
/// <param name="Text">The message body.</param>
public sealed record ChatMessageEvent(long SenderUserId, string SenderUsername, string? Channel, string Text) : IPluginEvent;

/// <summary>
/// Raised when a player's status (CHANGE_ACTION) is processed. Carries the map they moved onto —
/// DAN Mode uses this to start/stop bot-spectating the player.
/// </summary>
/// <param name="UserId">The player's user id.</param>
/// <param name="Action">Raw bancho action byte (idle/playing/watching/…).</param>
/// <param name="BeatmapMd5">MD5 of the beatmap the player is on, or empty.</param>
/// <param name="BeatmapId">Beatmap id the player is on, or 0 when unknown.</param>
/// <param name="Mods">Legacy mods bitmask the player has selected.</param>
/// <param name="GameMode">Combined gamemode byte (vanilla 0-3, relax +4, autopilot +8).</param>
public sealed record ActionChangedEvent(long UserId, byte Action, string BeatmapMd5, long BeatmapId, uint Mods, byte GameMode) : IPluginEvent
{
    /// <summary>The client's version (e.g. <c>b20240123.2</c>, or an old build's), empty when unknown.</summary>
    public string ClientVersion { get; init; } = string.Empty;

    /// <summary>
    /// Whether the host can read this client's spectator frames, so <see cref="SpectateFrameEvent.Action"/>
    /// is set for them: osu!stable builds whose frame bundles are in today's layout (from b20130513).
    /// False for older builds and IRC sessions.
    /// </summary>
    public bool CanCheckFrames { get; init; }
}

/// <summary>
/// Raised for each SPECTATE_FRAMES bundle a spectated host sends. <see cref="RawFrame"/> is the raw
/// replay-frame payload as the client sent it; <see cref="Action"/> is the bundle's replay action,
/// read by the host (old builds send other layouts), so a plugin needn't parse frames to see a pause.
/// </summary>
/// <param name="HostUserId">User id of the player being spectated (the frame source).</param>
/// <param name="RawFrame">The raw replay-frame-bundle payload bytes.</param>
public sealed record SpectateFrameEvent(long HostUserId, ReadOnlyMemory<byte> RawFrame) : IPluginEvent
{
    /// <summary>
    /// The bundle's replay action (<see cref="ReplayActions"/>, e.g. <see cref="ReplayActions.Pause"/>),
    /// or null when the host couldn't read the bundle.
    /// </summary>
    public byte? Action { get; init; }
}

/// <summary>The replay actions of a spectator frame bundle (<see cref="SpectateFrameEvent.Action"/>).</summary>
public static class ReplayActions
{
    /// <summary>Ordinary play.</summary>
    public const byte Standard = 0;

    /// <summary>The player started a new map (or restarted).</summary>
    public const byte NewSong = 1;

    /// <summary>The player skipped the intro.</summary>
    public const byte Skip = 2;

    /// <summary>The play finished.</summary>
    public const byte Completion = 3;

    /// <summary>The player failed.</summary>
    public const byte Fail = 4;

    /// <summary>The player paused.</summary>
    public const byte Pause = 5;

    /// <summary>The player unpaused.</summary>
    public const byte Unpause = 6;

    /// <summary>The player is back in song select.</summary>
    public const byte SongSelect = 7;

    /// <summary>The player is spectating someone else.</summary>
    public const byte WatchingOther = 8;
}

/// <summary>
/// Vetoable: raised just before a submitted score is persisted. A handler may
/// <see cref="VetoableEvent.Veto"/> it (DAN Mode rejecting a score that broke dan rules); the host
/// then rejects the submission. Runs on the score hot path — keep handlers cheap.
/// </summary>
public sealed class ScoreSubmittingEvent : VetoableEvent
{
    /// <summary>Creates the pre-persist score event.</summary>
    public ScoreSubmittingEvent(
        long userId,
        string beatmapMd5,
        byte gameMode,
        uint mods,
        double accuracy,
        int maxCombo,
        bool passed,
        string beatmapTitle)
    {
        UserId = userId;
        BeatmapMd5 = beatmapMd5;
        GameMode = gameMode;
        Mods = mods;
        Accuracy = accuracy;
        MaxCombo = maxCombo;
        Passed = passed;
        BeatmapTitle = beatmapTitle;
    }

    /// <summary>Id of the submitting player.</summary>
    public long UserId { get; }

    /// <summary>MD5 of the played beatmap.</summary>
    public string BeatmapMd5 { get; }

    /// <summary>Combined gamemode byte (vanilla 0-3, relax +4, autopilot +8).</summary>
    public byte GameMode { get; }

    /// <summary>Legacy mods bitmask used for the play.</summary>
    public uint Mods { get; }

    /// <summary>Accuracy as a fraction in [0, 1].</summary>
    public double Accuracy { get; }

    /// <summary>Max combo achieved.</summary>
    public int MaxCombo { get; }

    /// <summary>Whether the play passed (vs failed/retried).</summary>
    public bool Passed { get; }

    /// <summary>Human-readable beatmap title, for messages/logs.</summary>
    public string BeatmapTitle { get; }

    /// <summary>The played beatmap's id (0 when the host didn't set it).</summary>
    public long BeatmapId { get; init; }

    /// <summary>The played beatmap's set id (0 when the host didn't set it).</summary>
    public long BeatmapSetId { get; init; }

    /// <summary>The played difficulty's name (e.g. <c>~ INTRO-1st ~</c>), empty when the host didn't set it.</summary>
    public string BeatmapVersion { get; init; } = string.Empty;
}

/// <summary>
/// Raised after an osu!stable score has been stored (failed plays included), with the stored score's
/// id. Not raised for a score a plugin vetoed (<see cref="ScoreSubmittingEvent"/>) or the host refused.
/// osu!lazer scores go to the website and don't raise it.
/// </summary>
/// <param name="ScoreId">The stored score's id.</param>
/// <param name="UserId">The player.</param>
/// <param name="BeatmapId">The beatmap's id.</param>
/// <param name="BeatmapSetId">The beatmap's set id.</param>
/// <param name="BeatmapMd5">The beatmap's MD5.</param>
/// <param name="GameMode">Combined gamemode byte (vanilla 0-3, relax +4, autopilot +8).</param>
/// <param name="Mods">Legacy mods bitmask.</param>
/// <param name="Accuracy">Accuracy as a fraction in [0, 1].</param>
/// <param name="TotalScore">The score's points.</param>
/// <param name="MaxCombo">Max combo.</param>
/// <param name="Misses">Misses.</param>
/// <param name="Grade">Grade letter as the client sent it (<c>XH</c>, <c>X</c>, <c>SH</c>, <c>S</c>, <c>A</c> … <c>F</c>).</param>
/// <param name="Pp">pp awarded (0 on maps without ranked pp, e.g. Loved).</param>
/// <param name="Passed">Whether the play passed.</param>
/// <param name="ClientVersion">The client's version.</param>
public sealed record ScoreSubmittedEvent(
    long ScoreId,
    long UserId,
    long BeatmapId,
    long BeatmapSetId,
    string BeatmapMd5,
    byte GameMode,
    uint Mods,
    double Accuracy,
    long TotalScore,
    int MaxCombo,
    int Misses,
    string Grade,
    double Pp,
    bool Passed,
    string ClientVersion) : IPluginEvent
{
    /// <summary>The played difficulty's name.</summary>
    public string BeatmapVersion { get; init; } = string.Empty;

    /// <summary>Human-readable beatmap title (<c>Artist - Title [Version]</c>).</summary>
    public string BeatmapTitle { get; init; } = string.Empty;

    /// <summary>Whether the player is restricted (their scores are stored but hidden from others).</summary>
    public bool PlayerRestricted { get; init; }
}
