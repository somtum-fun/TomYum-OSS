namespace TomYum.Domain.Entities;

/// <summary>
/// Records that a user has unlocked a given achievement (medal). Achievement <em>definitions</em>
/// (id, icon, name, description, unlock condition) live in code — see the medal catalog in
/// <c>TomYum.Server</c> — so only the per-user unlock is persisted here. One row per unlocked medal.
/// </summary>
public sealed class UserAchievement
{
    /// <summary>The user who unlocked the medal.</summary>
    public int UserId { get; init; }

    /// <summary>The catalog id of the unlocked achievement.</summary>
    public int AchievementId { get; init; }

    /// <summary>When the medal was unlocked (UTC). Stored as <c>datetime(6)</c>.</summary>
    public DateTime UnlockedAt { get; set; }
}
