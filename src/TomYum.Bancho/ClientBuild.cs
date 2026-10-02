using System.Globalization;
using System.Text.RegularExpressions;

namespace TomYum.Bancho;

/// <summary>
/// An osu!stable build, from the version a client sends at login (<c>b20140624</c>,
/// <c>b20150101cuttingedge</c>, <c>b20240123.2</c>, <c>b20240123tourney</c>). Old builds (docs/old-clients.md)
/// speak older versions of the protocol and of the web endpoints; their build date says which.
/// </summary>
public sealed partial record ClientBuild(string Version, DateOnly? Date)
{
    /// <summary>
    /// Builds before this run on .NET Framework 2.0: no TLS 1.2, so they can't reach today's HTTPS
    /// sites (beatmap mirrors) and are served over plain HTTP. osu! moved stable to .NET 4 in November 2015.
    /// </summary>
    public static readonly DateOnly ModernFrom = new(2016, 1, 1);

    /// <summary>The build of <paramref name="version"/>; <see cref="Date"/> is null when there's no date in it.</summary>
    public static ClientBuild Parse(string? version)
    {
        version ??= string.Empty;
        var match = DatePattern().Match(version);
        DateOnly? date = match.Success
            && DateOnly.TryParseExact(match.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
        return new ClientBuild(version, date);
    }

    /// <summary>A build from before <see cref="ModernFrom"/> (an unknown date counts as today's client).</summary>
    public bool IsLegacy => Date is { } date && date < ModernFrom;

    [GeneratedRegex(@"^b(?<date>\d{8})", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();
}
