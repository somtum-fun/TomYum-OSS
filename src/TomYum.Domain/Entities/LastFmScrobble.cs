namespace TomYum.Domain.Entities;

/// <summary>A song the server scrobbled to a player's Last.fm (the recent ones are shown in their settings).</summary>
public sealed class LastFmScrobble
{
    public long Id { get; init; }

    public int UserId { get; init; }

    public string Artist { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>Where it was played: <c>stable</c> or <c>lazer</c>.</summary>
    public string Client { get; init; } = string.Empty;

    public int BeatmapId { get; init; }

    /// <summary>The time Last.fm files it under (when the play started).</summary>
    public DateTime ScrobbledAt { get; init; }
}
