using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TomYum.Persistence;
using TomYum.Persistence.OsuWeb;
using TomYum.Server.Achievements;

namespace TomYum.Server.OsuWeb;

/// <summary>One <c>osu_achievements</c> row.</summary>
public sealed record OsuWebMedalRow(int Id, string Name, string Slug, string Description, string Grouping, int Ordering, int Progression, int? Mode, string? Instructions = null);

/// <summary>
/// Medals on osu-web (docs/osu-web.md). osu-web's migrations leave its medal catalog
/// (<c>osu_achievements</c>) empty, and a profile only shows medals that are in it. The catalog is
/// osu!'s own: ppy's score processor awards lazer medals by osu!'s ids, and TomYum's stable medals
/// use the same ids (<see cref="MedalCatalog"/>). It's read from osu.ppy.sh (every profile page
/// carries the full list); without a connection, TomYum's own medals are written. Unlocks normally
/// go through osu-web's InterOp API (<see cref="OsuWebInterop"/>); this class also holds the
/// direct-insert fallback and the startup copy of earlier unlocks.
/// </summary>
public sealed partial class OsuWebMedals
{
    /// <summary>A public profile page (its initial data lists every medal).</summary>
    public const string OfficialListUrl = "https://osu.ppy.sh/users/2";

    private readonly OsuWebDbContext _osuWeb;
    private readonly TomYumDbContext _tomyum;

    public OsuWebMedals(OsuWebDbContext osuWeb, TomYumDbContext tomyum)
    {
        _osuWeb = osuWeb;
        _tomyum = tomyum;
    }

    /// <summary>TomYum's own medals as catalog rows (the fallback when osu!'s list can't be read).</summary>
    public static IReadOnlyList<OsuWebMedalRow> CatalogRows() => MedalCatalog.All.Select(ToRow).ToList();

    /// <summary>osu!'s medal list from a profile page's initial data, or null when it isn't there.</summary>
    public static IReadOnlyList<OsuWebMedalRow>? ParseOfficialList(string page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var match = InitialData().Match(page);
        if (!match.Success)
            return null;

        try
        {
            using var document = JsonDocument.Parse(WebUtility.HtmlDecode(match.Groups[1].Value));
            if (!document.RootElement.TryGetProperty("achievements", out var list) || list.ValueKind != JsonValueKind.Array)
                return null;

            var rows = new List<OsuWebMedalRow>();
            var perGroup = new Dictionary<(string, int), int>();
            foreach (var medal in list.EnumerateArray())
            {
                var grouping = medal.GetProperty("grouping").GetString() ?? string.Empty;
                var ordering = medal.GetProperty("ordering").GetInt32();
                // The list comes in the profile's order; progression orders medals within a row.
                var progression = perGroup[(grouping, ordering)] = perGroup.GetValueOrDefault((grouping, ordering)) + 1;
                rows.Add(new OsuWebMedalRow(
                    medal.GetProperty("id").GetInt32(),
                    OsuWebBeatmaps.Truncate(medal.GetProperty("name").GetString() ?? string.Empty, 40),
                    medal.GetProperty("slug").GetString() ?? string.Empty,
                    medal.GetProperty("description").GetString() ?? string.Empty,
                    OsuWebBeatmaps.Truncate(grouping, 30),
                    ordering,
                    Math.Min(progression, 255),
                    medal.TryGetProperty("mode", out var mode) && mode.ValueKind == JsonValueKind.String ? ModeOf(mode.GetString()!) : null,
                    medal.TryGetProperty("instructions", out var instructions) && instructions.ValueKind == JsonValueKind.String ? instructions.GetString() : null));
            }
            return rows.Count > 0 && rows.All(r => r.Id > 0 && r.Slug.Length > 0) ? rows : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Downloads osu!'s medal list; null when osu.ppy.sh can't be reached or the page changed.</summary>
    public static async Task<IReadOnlyList<OsuWebMedalRow>?> FetchOfficialListAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, OfficialListUrl);
            request.Headers.UserAgent.ParseAdd("TomYum (+https://github.com/sundeiii/TomYum)");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            return ParseOfficialList(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the catalog: each row is added or brought up to date. With osu!'s full list
    /// (<paramref name="complete"/>), medals that aren't osu!'s (TomYum's earlier numbering) are
    /// removed. Returns how many rows were added or changed.
    /// </summary>
    public async Task<int> WriteCatalogAsync(IReadOnlyList<OsuWebMedalRow> rows, bool complete, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var changed = 0;
        foreach (var row in rows)
        {
            changed += await _osuWeb.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO osu_achievements
                  (achievement_id, name, slug, description, image, `grouping`, ordering, progression, enabled, mode, quest_instructions)
                VALUES ({row.Id}, {row.Name}, {row.Slug}, {row.Description}, {row.Slug + ".png"}, {row.Grouping},
                  {row.Ordering}, {row.Progression}, 1, {row.Mode}, {row.Instructions})
                ON DUPLICATE KEY UPDATE name = VALUES(name), slug = VALUES(slug), description = VALUES(description),
                  image = VALUES(image), `grouping` = VALUES(`grouping`), ordering = VALUES(ordering),
                  progression = VALUES(progression), enabled = 1, mode = VALUES(mode),
                  quest_instructions = COALESCE(VALUES(quest_instructions), quest_instructions)
                """,
                cancellationToken).ConfigureAwait(false) > 0 ? 1 : 0;
        }

        if (complete)
        {
            var ids = string.Join(',', rows.Select(r => r.Id.ToString(CultureInfo.InvariantCulture)));
#pragma warning disable EF1002 // numbers only
            changed += await _osuWeb.Database.ExecuteSqlRawAsync(
                $"DELETE FROM osu_achievements WHERE achievement_id NOT IN ({ids})", cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
        }
        return changed;
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
    /// Removes the website copy of a TomYum unlock made under its earlier (bancho.py) id: the row
    /// with that id, recorded within a few seconds of TomYum's own record (a medal ppy's processor
    /// gave with the same number at another time is left alone).
    /// </summary>
    public async Task<int> RemoveLegacyCopyAsync(int userId, int legacyId, DateTime unlockedAt, CancellationToken cancellationToken = default)
    {
        var from = unlockedAt.AddSeconds(-5);
        var to = unlockedAt.AddSeconds(5);
        return await _osuWeb.Database.ExecuteSqlAsync(
            $"DELETE FROM osu_user_achievements WHERE user_id = {userId} AND achievement_id = {legacyId} AND `date` BETWEEN {from} AND {to}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes "unlocked the … medal!" entries from players' recent activity for medals they don't
    /// have (anymore): left over from before medals moved to osu!'s ids, or from unlocks taken back.
    /// Matched on the exact name the website wrote (as is, or HTML-escaped). Returns how many.
    /// </summary>
    public async Task<int> RemoveStaleMedalEventsAsync(CancellationToken cancellationToken = default) =>
        await _osuWeb.Database.ExecuteSqlRawAsync(
            """
            DELETE e FROM osu_events e
            WHERE e.text LIKE '% unlocked the "<b>%</b>" medal!%'
              AND NOT EXISTS (
                SELECT 1 FROM osu_user_achievements ua
                JOIN osu_achievements a ON a.achievement_id = ua.achievement_id
                WHERE ua.user_id = e.user_id
                  AND (LOCATE(CAST(CONCAT('"<b>', a.name, '</b>" medal!') AS BINARY), CAST(e.text AS BINARY)) > 0
                    OR LOCATE(CAST(CONCAT('"<b>', REPLACE(REPLACE(REPLACE(REPLACE(a.name, '&', '&amp;'), '''', '&#039;'), '"', '&quot;'), '<', '&lt;'), '</b>" medal!') AS BINARY), CAST(e.text AS BINARY)) > 0))
            """,
            cancellationToken).ConfigureAwait(false);

    /// <summary>Removes a website unlock (used when TomYum's own medals are recalculated).</summary>
    public Task<int> RemoveUnlockAsync(int userId, int medalId, CancellationToken cancellationToken = default) =>
        _osuWeb.Database.ExecuteSqlAsync(
            $"DELETE FROM osu_user_achievements WHERE user_id = {userId} AND achievement_id = {medalId}", cancellationToken);

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
            _ when slug.StartsWith("osu-combo-", StringComparison.Ordinal) =>
                ("Skill & Dedication", 0, medal.Id, (int?)0),
            _ when slug.Contains("-skill-pass-", StringComparison.Ordinal) =>
                ("Skill & Dedication", 1, Tier(slug), (int?)ModeOf(slug[..slug.IndexOf('-', StringComparison.Ordinal)])),
            _ when slug.Contains("-skill-fc-", StringComparison.Ordinal) =>
                ("Skill & Dedication", 2, Tier(slug), (int?)ModeOf(slug[..slug.IndexOf('-', StringComparison.Ordinal)])),
            _ => ("Mod Introduction", 0, medal.Id - 118, (int?)null),
        };

        return new OsuWebMedalRow(medal.Id, OsuWebBeatmaps.Truncate(medal.Name, 40), slug, medal.Description, grouping, ordering, progression, mode);
    }

    private static int Tier(string slug) => int.Parse(slug[(slug.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture);

    private static int? ModeOf(string ruleset) => ruleset switch
    {
        "osu" => 0,
        "taiko" => 1,
        "fruits" => 2,
        "mania" => 3,
        _ => null,
    };

    [GeneratedRegex("data-initial-data=\"([^\"]*\\bachievements\\b[^\"]*)\"")]
    private static partial Regex InitialData();
}
