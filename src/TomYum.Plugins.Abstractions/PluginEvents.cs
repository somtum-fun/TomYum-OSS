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
public sealed record ActionChangedEvent(long UserId, byte Action, string BeatmapMd5, long BeatmapId, uint Mods, byte GameMode) : IPluginEvent;

/// <summary>
/// Raised for each SPECTATE_FRAMES bundle a spectated host sends. <see cref="RawFrame"/> is the raw
/// replay-frame payload; plugins that need frame detail (DAN pause detection) decode it themselves,
/// since the host relays frames verbatim without parsing.
/// </summary>
/// <param name="HostUserId">User id of the player being spectated (the frame source).</param>
/// <param name="RawFrame">The raw replay-frame-bundle payload bytes.</param>
public sealed record SpectateFrameEvent(long HostUserId, ReadOnlyMemory<byte> RawFrame) : IPluginEvent;

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
}
