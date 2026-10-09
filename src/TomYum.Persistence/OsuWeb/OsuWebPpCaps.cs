using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>
/// The pp cap (basic anti-cheat): a passed play worth more than its mode's cap gets the player restricted,
/// unless they're whitelisted. Staff set it in the website's admin panel (Admin panel > Anti-cheat), which
/// keeps it in <c>tomyum_site_settings</c> (<c>ppcap_enabled</c>, <c>ppcap_osu</c> … <c>ppcap_autopilot</c>);
/// unset values are these defaults. A cap of 0 means no cap for that mode.
/// </summary>
/// <param name="Enabled">Whether plays are checked at all.</param>
/// <param name="Rulesets">The caps for osu!, taiko, catch and mania (by ruleset id), without Relax or Autopilot.</param>
/// <param name="Relax">The cap for Relax plays (every ruleset).</param>
/// <param name="Autopilot">The cap for Autopilot plays.</param>
public sealed record PpCapSettings(bool Enabled, IReadOnlyList<double> Rulesets, double Relax, double Autopilot)
{
    /// <summary>On, with 1500pp for each mode and 2500pp for Relax and Autopilot.</summary>
    public static PpCapSettings Default { get; } = new(true, [1500, 1500, 1500, 1500], 2500, 2500);

    /// <summary>The setting names, after <c>ppcap_</c>.</summary>
    public static readonly IReadOnlyList<string> RulesetKeys = ["osu", "taiko", "catch", "mania"];

    /// <summary>The cap that applies to a play (0: none).</summary>
    /// <param name="ruleset">The play's ruleset id.</param>
    /// <param name="mods">The play's mod acronyms.</param>
    public double CapFor(int ruleset, IReadOnlyCollection<string> mods)
    {
        ArgumentNullException.ThrowIfNull(mods);
        if (mods.Contains("RX", StringComparer.OrdinalIgnoreCase))
            return Relax;
        if (mods.Contains("AP", StringComparer.OrdinalIgnoreCase))
            return Autopilot;
        return ruleset >= 0 && ruleset < Rulesets.Count ? Rulesets[ruleset] : 0;
    }

    /// <summary>The settings from <c>tomyum_site_settings</c> rows (name → value); missing or bad ones are the defaults.</summary>
    /// <param name="rows">The <c>ppcap_*</c> rows.</param>
    public static PpCapSettings From(IReadOnlyDictionary<string, string?> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        double Cap(string key, double fallback) =>
            rows.TryGetValue("ppcap_" + key, out var value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var cap) && cap >= 0
                ? cap
                : fallback;
        return new PpCapSettings(
            rows.TryGetValue("ppcap_enabled", out var enabled) && enabled is not null ? enabled.Trim() != "0" : Default.Enabled,
            RulesetKeys.Select((key, i) => Cap(key, Default.Rulesets[i])).ToList(),
            Cap("relax", Default.Relax),
            Cap("autopilot", Default.Autopilot));
    }
}

/// <summary>Reads <see cref="PpCapSettings"/> from the website's site settings.</summary>
public sealed class OsuWebPpCaps
{
    private readonly OsuWebDbContext _db;

    public OsuWebPpCaps(OsuWebDbContext db) => _db = db;

    /// <summary>The current settings (the defaults when the website has no settings table).</summary>
    public async Task<PpCapSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var rows = await _db.Database.SqlQueryRaw<SettingRow>(
                    "SELECT name AS Name, value AS Value FROM tomyum_site_settings WHERE name LIKE 'ppcap\\_%'")
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return PpCapSettings.From(rows.ToDictionary(r => r.Name, r => r.Value, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is MySqlConnector.MySqlException { ErrorCode: MySqlConnector.MySqlErrorCode.NoSuchTable })
        {
            return PpCapSettings.Default;
        }
    }

    private sealed class SettingRow
    {
        public string Name { get; set; } = string.Empty;

        public string? Value { get; set; }
    }
}
