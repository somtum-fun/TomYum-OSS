using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>A profile badge (<c>osu_badges</c>): the image's file name, its tooltip and an optional link.</summary>
public sealed class OsuWebBadge
{
    public string Image { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public DateTime? Awarded { get; set; }
}

/// <summary>
/// Profile badges, as the website stores them: one row per player and image (the image is a file
/// name under the website's badge image prefix, e.g. <c>contributor.png</c>).
/// </summary>
public sealed class OsuWebBadges
{
    /// <summary>The longest image name, description or link the table takes.</summary>
    public const int MaxLength = 255;

    private readonly OsuWebDbContext _db;

    public OsuWebBadges(OsuWebDbContext db)
    {
        _db = db;
    }

    /// <summary>The player's badges, oldest first (the order the profile shows them).</summary>
    public async Task<IReadOnlyList<OsuWebBadge>> ListAsync(int userId, CancellationToken cancellationToken = default) =>
        await _db.Database.SqlQueryRaw<OsuWebBadge>(
                "SELECT image AS Image, description AS Description, url AS Url, awarded AS Awarded FROM osu_badges WHERE user_id = {0} ORDER BY awarded, image",
                userId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Gives the player a badge; false when they already have one with that image.</summary>
    public async Task<bool> AddAsync(int userId, string image, string description, string? url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var added = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT IGNORE INTO osu_badges (user_id, image, description, url, awarded)
            VALUES ({userId}, {Cut(image)}, {Cut(description)}, {Cut(url ?? string.Empty)}, UTC_TIMESTAMP())
            """, cancellationToken).ConfigureAwait(false);
        return added > 0;
    }

    /// <summary>Takes a badge away; false when the player didn't have it.</summary>
    public async Task<bool> RemoveAsync(int userId, string image, CancellationToken cancellationToken = default) =>
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM osu_badges WHERE user_id = {userId} AND image = {image}", cancellationToken).ConfigureAwait(false) > 0;

    private static string Cut(string text)
    {
        text = text.Trim();
        return text.Length > MaxLength ? text[..MaxLength] : text;
    }
}
