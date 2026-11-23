using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace TomYum.Persistence.OsuWeb;

/// <summary>One beatmap mirror as staff set it in the website's admin panel (Beatmap mirrors).</summary>
/// <param name="Name">What staff call it.</param>
/// <param name="Url">Its address (https). Lookups ask <c>api/v2/md5|b|s/…</c> under it (osu!'s API v2 answers, as
/// catboy.best gives them).</param>
/// <param name="Lookups">Whether map lookups ask it.</param>
/// <param name="Downloads">Whether players' downloads (<c>/d/</c>) are sent to it.</param>
/// <param name="DownloadPath">Where a set is under it, <c>{id}</c> the set (<c>/d/{id}</c> when empty).</param>
/// <param name="RequestsPerMinute">How many lookups a minute this server makes to it.</param>
/// <param name="Priority">Lower first.</param>
/// <param name="Enabled">Whether it's used at all.</param>
public sealed record BeatmapMirrorEntry(
    string Name,
    string Url,
    bool Lookups = true,
    bool Downloads = false,
    string? DownloadPath = null,
    int RequestsPerMinute = 60,
    int Priority = 100,
    bool Enabled = true)
{
    /// <summary>The address as a URI with a trailing slash, or null when it isn't one this server may ask (<see cref="IsAllowed"/>).</summary>
    public Uri? BaseUri => Uri.TryCreate(Url?.Trim(), UriKind.Absolute, out var uri) && IsAllowed(uri)
        ? new Uri(uri.AbsoluteUri.TrimEnd('/') + "/")
        : null;

    /// <summary>
    /// Whether the server may ask <paramref name="uri"/>: https, no user name or password in it, and not this
    /// machine or a private network (a staff-entered address must not reach the server's own services).
    /// </summary>
    public static bool IsAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Host.Length == 0)
            return false;
        var host = uri.IdnHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.', StringComparison.Ordinal))
            return false;
        return !IPAddress.TryParse(host.Trim('[', ']'), out var address) || IsPublic(address);
    }

    private static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal);
        var b = address.GetAddressBytes();
        return !(b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127) || b[0] == 0 || b[0] >= 224);
    }
}

/// <summary>
/// The beatmap mirrors staff set in the website's admin panel: osu-web's <c>tomyum_site_settings</c> row
/// <c>beatmap_mirrors_json</c> (an array of <see cref="BeatmapMirrorEntry"/>, snake_case). Not set (or not valid
/// JSON): null, and the server uses its own <c>Mirror:*</c> and <c>Direct:*</c> settings.
/// </summary>
public sealed class OsuWebBeatmapMirrors
{
    /// <summary>The settings row.</summary>
    public const string Setting = "beatmap_mirrors_json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly OsuWebDbContext _db;

    public OsuWebBeatmapMirrors(OsuWebDbContext db) => _db = db;

    /// <summary>The mirrors as set, or null when the panel never set any.</summary>
    public async Task<IReadOnlyList<BeatmapMirrorEntry>?> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var row = await _db.Database.SqlQuery<string>(
                    $"SELECT value AS Value FROM tomyum_site_settings WHERE name = {Setting}")
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(row) ? null : Parse(row);
        }
        catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.NoSuchTable)
        {
            return null;
        }
    }

    /// <summary>The list in <paramref name="json"/>, or null when it isn't one.</summary>
    public static IReadOnlyList<BeatmapMirrorEntry>? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<BeatmapMirrorEntry>>(json, JsonOptions)?.Where(m => m is not null).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The list as the panel stores it.</summary>
    public static string Serialize(IEnumerable<BeatmapMirrorEntry> mirrors) => JsonSerializer.Serialize(mirrors, JsonOptions);
}
