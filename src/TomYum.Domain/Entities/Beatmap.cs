namespace TomYum.Domain.Entities;

/// <summary>
/// A single difficulty within a beatmap set.
/// </summary>
/// <remarks>
/// Server-local (custom-submitted) beatmaps reserve ids at or above
/// <see cref="CustomIdFloor"/> so they never collide with official osu! ids.
/// See <c>memories/architecture/decisions/0009-custom-beatmap-ids.md</c>.
/// Scaffolding stub.
/// </remarks>
public sealed class Beatmap
{
    /// <summary>
    /// Inclusive lower bound for server-local beatmap and set ids. Ids at or above this
    /// value are owned by this server; ids below it mirror official osu! content.
    /// </summary>
    public const int CustomIdFloor = 100_000_000;

    /// <summary>Beatmap (difficulty) id.</summary>
    public int Id { get; init; }

    /// <summary>Owning beatmap set id.</summary>
    public int SetId { get; init; }

    /// <summary>MD5 of the <c>.osu</c> file, used as the wire identity in both protocols.</summary>
    public string Md5 { get; init; } = string.Empty;

    /// <summary>Whether this beatmap id is in the server-local custom range.</summary>
    public bool IsCustom => Id >= CustomIdFloor;

    // TODO: title/artist/version, status, star rating, mode, owner, etc.
}
