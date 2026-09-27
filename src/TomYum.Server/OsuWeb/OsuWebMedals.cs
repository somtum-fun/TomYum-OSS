using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Achievements;

namespace TomYum.Server.OsuWeb;

/// <summary>One <c>osu_achievements</c> row for a TomYum medal.</summary>
public sealed record OsuWebMedalRow(int Id, string Name, string Slug, string Description, string Grouping, int Ordering, int Progression, int? Mode);

/// <summary>
/// Medals on osu-web (docs/osu-web.md). osu-web's migrations leave its medal catalog
/// (<c>osu_achievements</c>) empty, and a profile only shows medals that are in it, so TomYum adds
/// its catalog (ids, names and slugs match osu!'s, so osu-web's icon URLs work unchanged). Unlocks
/// normally go through osu-web's InterOp API (<see cref="OsuWebInterop"/>); this class also holds
/// the direct-insert fallback and the startup copy of earlier unlocks.
/// </summary>
public sealed class OsuWebMedals
{
    private readonly OsuWebDbContext _osuWeb;
    private readonly TomYumDbContext _tomyum;

    public OsuWebMedals(OsuWebDbContext osuWeb, TomYumDbContext tomyum)
    {
        _osuWeb = osuWeb;
        _tomyum = tomyum;
    }

    /// <summary>
    /// TomYum's catalog as osu-web rows, grouped the way the profile page lays them out
    /// (by <c>grouping</c>, then one row per <c>ordering</c>, sorted by <c>progression</c>).
    /// </summary>
    public static IReadOnlyList<OsuWebMedalRow> CatalogRows() => MedalCatalog.All.Select(ToRow).ToList();

    /// <summary>
    /// Adds the catalog rows osu-web doesn't have (existing rows, e.g. imported official data, are kept).
    /// Not <c>INSERT IGNORE</c>: that would silently clamp bad values instead of failing.
    /// </summary>
    public async Task<int> WriteCatalogAsync(CancellationToken cancellationToken = default)
    {
        var existing = (await _osuWeb.Database.SqlQuery<int>($"SELECT achievement_id AS Value FROM osu_achievements")
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet();

        var added = 0;
        foreach (var row in CatalogRows().Where(r => !existing.Contains(r.Id)))
        {
            await _osuWeb.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO osu_achievements
                  (achievement_id, name, slug, description, image, `grouping`, ordering, progression, enabled, mode)
                VALUES ({row.Id}, {row.Name}, {row.Slug}, {row.Description}, {row.Slug + ".png"}, {row.Grouping},
                  {row.Ordering}, {row.Progression}, 1, {row.Mode})
                """,
                cancellationToken).ConfigureAwait(false);
            added++;
        }
        return added;
    }

    /// <summary>
    /// Records an unlock directly in <c>osu_user_achievements</c> (idempotent). Used when the InterOp
    /// API isn't configured or fails; unlike the API it adds no "unlocked a medal" activity entry.
    /// </summary>
    public Task InsertUnlockAsync(int userId, int medalId, int? beatmapId, DateTime unlockedAt, CancellationToken cancellationToken = default) =>
        _osuWeb.Database.ExecuteSqlAsync(
            $"INSERT INTO osu_user_achievements (user_id, achievement_id, date, beatmap_id) VALUES ({userId}, {medalId}, {unlockedAt}, {beatmapId}) ON DUPLICATE KEY UPDATE user_id = user_id",
            cancellationToken);

    /// <summary>
    /// Copies unlocks TomYum recorded earlier (e.g. before the osu-web link, or while osu-web was
    /// unreachable) for players that exist on osu-web. Returns how many were added.
    /// </summary>
    public async Task<int> CopyUnlocksAsync(CancellationToken cancellationToken = default)
    {
        var unlocks = await _tomyum.UserAchievements.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var accounts = (await _osuWeb.Users.AsNoTracking().Select(u => u.UserId).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet();

        var existing = (await _osuWeb.Database
                .SqlQuery<string>($"SELECT CONCAT(user_id, ':', achievement_id) AS Value FROM osu_user_achievements")
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var added = 0;
        foreach (var unlock in unlocks.Where(u => u.UserId > 0 && accounts.Contains((uint)u.UserId)
                     && !existing.Contains(string.Create(CultureInfo.InvariantCulture, $"{u.UserId}:{u.AchievementId}"))))
        {
            await _osuWeb.Database.ExecuteSqlAsync(
                $"INSERT INTO osu_user_achievements (user_id, achievement_id, date) VALUES ({unlock.UserId}, {unlock.AchievementId}, {unlock.UnlockedAt})",
                cancellationToken).ConfigureAwait(false);
            added++;
        }
        return added;
    }

    private static OsuWebMedalRow ToRow(Medal medal)
    {
        var slug = medal.File;
        var (grouping, ordering, progression, mode) = slug switch
        {
            // progression is a tinyint: order the combo medals 1-4 by their combo, not by the combo itself.
            _ when slug.StartsWith("osu-combo-", StringComparison.Ordinal) =>
                ("Skill", 0, ComboTier(slug), (int?)0),
            _ when slug.Contains("-skill-pass-", StringComparison.Ordinal) =>
                ("Skill", 1, Tier(slug), (int?)ModeOf(slug)),
            _ when slug.Contains("-skill-fc-", StringComparison.Ordinal) =>
                ("Skill", 2, Tier(slug), (int?)ModeOf(slug)),
            _ => ("Mod Introduction", 0, medal.Id, (int?)null),
        };

        return new OsuWebMedalRow(medal.Id, OsuWebBeatmaps.Truncate(medal.Name, 40), slug, medal.Description, grouping, ordering, progression, mode);
    }

    private static int ComboTier(string slug) =>
        MedalCatalog.All.Where(m => m.File.StartsWith("osu-combo-", StringComparison.Ordinal))
            .Select(m => int.Parse(m.File["osu-combo-".Length..], CultureInfo.InvariantCulture))
            .Order()
            .ToList()
            .IndexOf(int.Parse(slug["osu-combo-".Length..], CultureInfo.InvariantCulture)) + 1;

    private static int Tier(string slug) => int.Parse(slug[(slug.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture);

    private static int ModeOf(string slug) => slug[..slug.IndexOf('-', StringComparison.Ordinal)] switch
    {
        "taiko" => 1,
        "fruits" => 2,
        "mania" => 3,
        _ => 0,
    };
}
