using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TomYum.Domain.Entities;
using TomYum.Domain.Enums;
using TomYum.Persistence;

namespace TomYum.Tools.RecalcPp;

/// <summary>
/// Backfills the <c>beatmaps</c> table for scores whose beatmap row is missing.
/// Queries every distinct MD5 in <c>scores</c> that has no corresponding row in
/// <c>beatmaps</c>, resolves each via the osu! v1 API (or the keyless osu.direct
/// mirror), and upserts the result. Run once after upgrading from the old in-memory-only
/// BeatmapService, then the recalc join will find all scored maps.
/// </summary>
internal sealed class BeatmapBackfiller
{
    private const string OfficialBase = "https://old.ppy.sh/api/";
    private const string MirrorBase   = "https://osu.direct/api/";

    private readonly TomYumDbContext _db;
    private readonly HttpClient _http;
    private readonly ILogger<BeatmapBackfiller> _log;
    private readonly string? _apiKey;
    private readonly bool _dryRun;

    public BeatmapBackfiller(
        TomYumDbContext db,
        HttpClient http,
        ILogger<BeatmapBackfiller> log,
        string? apiKey,
        bool dryRun)
    {
        _db     = db;
        _http   = http;
        _log    = log;
        _apiKey = apiKey;
        _dryRun = dryRun;
    }

    public async Task<int> BackfillAsync(CancellationToken cancellationToken)
    {
        // All distinct MD5s referenced by scores that lack a beatmap row.
        var missingMd5s = await _db.Scores
            .Where(s => !_db.Beatmaps.Any(b => b.Id == s.BeatmapId))
            .Select(s => s.BeatmapMd5)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        _log.LogInformation("Found {Count} beatmap MD5(s) without a DB row", missingMd5s.Count);

        if (missingMd5s.Count == 0)
            return 0;

        var inserted = 0;
        var failed   = 0;
        var total    = missingMd5s.Count;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var md5     = missingMd5s[i];
            var beatmap = await FetchAsync(md5, cancellationToken).ConfigureAwait(false);

            if (beatmap is null)
            {
                _log.LogWarning("Could not resolve MD5 {Md5} — skipping", md5);
                failed++;
            }
            else
            {
                if (!_dryRun)
                    await UpsertAsync(beatmap, cancellationToken).ConfigureAwait(false);

                inserted++;
            }

            Console.Write($"\r  Beatmaps: {i + 1}/{total} ({(i + 1) * 100 / total}%)   ");
        }

        Console.WriteLine();
        _log.LogInformation("Backfill complete — inserted/updated: {Ok}, failed: {Fail}", inserted, failed);
        return inserted;
    }

    private async Task<Beatmap?> FetchAsync(string md5, CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrEmpty(_apiKey) ? MirrorBase : OfficialBase;
        var url     = string.IsNullOrEmpty(_apiKey)
            ? $"{baseUrl}get_beatmaps?h={WebUtility.UrlEncode(md5)}"
            : $"{baseUrl}get_beatmaps?k={WebUtility.UrlEncode(_apiKey)}&h={WebUtility.UrlEncode(md5)}";

        try
        {
            using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return null;

            return MapElement(root[0]);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "API fetch failed for {Md5}", md5);
            return null;
        }
    }

    private async Task UpsertAsync(Beatmap beatmap, CancellationToken cancellationToken)
    {
        var existing = await _db.Beatmaps
            .FirstOrDefaultAsync(b => b.Id == beatmap.Id, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _db.Beatmaps.Add(beatmap);
        }
        else
        {
            existing.SetId       = beatmap.SetId;
            existing.Md5         = beatmap.Md5;
            existing.Artist      = beatmap.Artist;
            existing.Title       = beatmap.Title;
            existing.Version     = beatmap.Version;
            existing.Creator     = beatmap.Creator;
            existing.Mode        = beatmap.Mode;
            existing.StarRating  = beatmap.StarRating;
            existing.TotalLength = beatmap.TotalLength;
            existing.MaxCombo    = beatmap.MaxCombo;
            existing.LastUpdate  = beatmap.LastUpdate;
            if (!existing.StatusFrozen)
                existing.Status = beatmap.Status;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
    }

    // ── JSON helpers ── both old.ppy.sh (quoted strings) and osu.direct (numbers) ──────────

    private static Beatmap MapElement(JsonElement e) => new()
    {
        Md5         = GetStr(e, "file_md5"),
        Id          = GetInt(e, "beatmap_id"),
        SetId       = GetInt(e, "beatmapset_id"),
        Artist      = GetStr(e, "artist"),
        Title       = GetStr(e, "title"),
        Version     = GetStr(e, "version"),
        Creator     = GetStr(e, "creator"),
        Status      = RankedStatusExtensions.FromOsuApi(GetInt(e, "approved")),
        Mode        = GetInt(e, "mode"),
        StarRating  = GetDouble(e, "difficultyrating"),
        TotalLength = GetInt(e, "total_length"),
        MaxCombo    = GetInt(e, "max_combo"),
        LastUpdate  = GetDate(e, "last_update"),
    };

    private static string GetStr(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return "";
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
    }

    private static int GetInt(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;
        return 0;
    }

    private static double GetDouble(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        return 0;
    }

    private static DateTimeOffset GetDate(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return DateTimeOffset.MinValue;
        var raw = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d : DateTimeOffset.MinValue;
    }
}
