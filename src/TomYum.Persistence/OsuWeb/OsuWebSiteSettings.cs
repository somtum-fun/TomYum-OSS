using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// Settings staff change in the website's admin panel (osu-web's <c>tomyum_site_settings</c>, name and value).
/// A setting that was never saved isn't there: the caller's default applies.
/// </summary>
public sealed class OsuWebSiteSettings
{
    private readonly OsuWebDbContext _db;

    public OsuWebSiteSettings(OsuWebDbContext db) => _db = db;

    /// <summary>The settings whose names start with <paramref name="prefix"/> (none without the table).</summary>
    public async Task<IReadOnlyDictionary<string, string?>> GetAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        var like = prefix.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal) + "%";
        try
        {
            var rows = await _db.Database.SqlQuery<SettingRow>(
                    $"SELECT name AS Name, value AS Value FROM tomyum_site_settings WHERE name LIKE {like}")
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.ToDictionary(r => r.Name, r => r.Value, StringComparer.Ordinal);
        }
        catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.NoSuchTable)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }
    }

    private sealed record SettingRow(string Name, string? Value);
}
