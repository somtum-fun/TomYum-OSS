namespace TomYum.Domain.Entities;

/// <summary>A player's Last.fm account, which their plays are scrobbled to.</summary>
public sealed class LastFmLink
{
    public int UserId { get; init; }

    /// <summary>Their Last.fm name.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Last.fm's session key: lets the server scrobble for them until they disconnect.</summary>
    public string SessionKey { get; set; } = string.Empty;

    public DateTime LinkedAt { get; set; }
}
