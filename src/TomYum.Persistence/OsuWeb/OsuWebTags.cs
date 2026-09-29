using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>A beatmap tag as osu! defines it (e.g. <c>style/clean</c>), for lazer's tag voting.</summary>
public sealed record BeatmapTagDefinition(long Id, string Name, int? RulesetId, string Description);

/// <summary>The website's list of beatmap tags (<c>tags</c>), kept the same as osu!'s (same ids).</summary>
public sealed class OsuWebTags
{
    private readonly OsuWebDbContext _db;

    public OsuWebTags(OsuWebDbContext db)
    {
        _db = db;
    }

    /// <summary>Adds new tags and updates changed ones; returns how many were added or changed.</summary>
    public async Task<int> UpsertAsync(IReadOnlyCollection<BeatmapTagDefinition> tags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var existing = (await _db.Database.SqlQuery<BeatmapTagDefinition>(
                $"SELECT CAST(id AS SIGNED) AS Id, name AS Name, CAST(ruleset_id AS SIGNED) AS RulesetId, description AS Description FROM tags")
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(t => t.Id);

        var changed = 0;
        foreach (var tag in tags)
        {
            if (existing.TryGetValue(tag.Id, out var current) && current == tag)
                continue;
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO tags (id, name, ruleset_id, description, created_at, updated_at)
                VALUES ({tag.Id}, {tag.Name}, {tag.RulesetId}, {tag.Description}, UTC_TIMESTAMP(), UTC_TIMESTAMP())
                ON DUPLICATE KEY UPDATE name = VALUES(name), ruleset_id = VALUES(ruleset_id), description = VALUES(description), updated_at = UTC_TIMESTAMP()
                """, cancellationToken).ConfigureAwait(false);
            changed++;
        }
        return changed;
    }
}
