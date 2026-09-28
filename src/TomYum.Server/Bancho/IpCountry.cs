using System.Globalization;
using System.IO.Compression;
using System.Net;
using MaxMind.Db;
using TomYum.Server.Admin;

namespace TomYum.Server.Bancho;

/// <summary>
/// A player's country when Cloudflare doesn't say (no <c>CF-IPCountry</c>, or <c>XX</c>/<c>T1</c>):
/// looked up from the client's address in DB-IP's free "IP to Country Lite" database (CC BY 4.0,
/// https://db-ip.com), downloaded to <c>GeoIp:Path</c> (default <c>data/geoip</c>) and refreshed
/// monthly by <see cref="IpCountryUpdater"/>. Without the file every lookup is simply unknown.
/// </summary>
public sealed class IpCountry : IDisposable
{
    /// <summary>The download client's name.</summary>
    public const string HttpClientName = "GeoIp";

    private const string FileName = "dbip-country-lite.mmdb";
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(32);

    private readonly IHttpClientFactory? _http;
    private readonly TimeProvider _time;
    private readonly ILogger<IpCountry> _logger;
    private readonly string _directory;
    private readonly SemaphoreSlim _updating = new(1, 1);
    private volatile Reader? _reader;

    public IpCountry(IConfiguration configuration, TimeProvider time, ILogger<IpCountry> logger, IHttpClientFactory? http = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _directory = Path.GetFullPath(configuration["GeoIp:Path"] is { Length: > 0 } path ? path : "data/geoip");
        _time = time;
        _logger = logger;
        _http = http;
        Open(Path.Combine(_directory, FileName));
    }

    /// <summary>Whether a database is loaded.</summary>
    public bool IsLoaded => _reader is not null;

    /// <summary>A usable country from a <c>CF-IPCountry</c> header: two letters, not unknown (<c>XX</c>) or Tor (<c>T1</c>).</summary>
    public static string? FromHeader(string? header)
    {
        var code = (header ?? string.Empty).Trim().ToLowerInvariant();
        return code.Length == 2 && code.All(char.IsAsciiLetterLower) && code is not ("xx" or "t1") ? code : null;
    }

    /// <summary>The country (lower-case ISO code) of a public address, or null.</summary>
    public string? Lookup(string? address)
    {
        if (_reader is not { } reader || !IPAddress.TryParse(address, out var ip) || !BadgeImages.IsPublic(ip))
            return null;
        try
        {
            var found = reader.Find<Dictionary<string, object>>(ip);
            return found is not null && found.TryGetValue("country", out var country) && country is Dictionary<string, object> fields
                   && fields.TryGetValue("iso_code", out var code) && code is string iso
                ? FromHeader(iso)
                : null;
        }
        catch (InvalidDatabaseException ex)
        {
            _logger.LogWarning(ex, "GeoIP: the country database is damaged.");
            return null;
        }
    }

    /// <summary>The header's country, else the address's, else <paramref name="fallback"/>.</summary>
    public string Resolve(string? header, string? address, string fallback) =>
        FromHeader(header) ?? Lookup(address) ?? fallback;

    /// <summary>Downloads this month's database when there's none or it's over a month old.</summary>
    public async Task UpdateAsync(CancellationToken cancellationToken = default)
    {
        if (_http is null)
            return;
        var path = Path.Combine(_directory, FileName);
        await _updating.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && _time.GetUtcNow() - File.GetLastWriteTimeUtc(path) < MaxAge)
                return;

            Directory.CreateDirectory(_directory);
            var now = _time.GetUtcNow().UtcDateTime;
            using var client = _http.CreateClient(HttpClientName);
            // A new month's file appears in the first days of the month: fall back to last month's.
            foreach (var month in new[] { now, now.AddMonths(-1) })
            {
                var url = string.Create(CultureInfo.InvariantCulture, $"https://download.db-ip.com/free/dbip-country-lite-{month:yyyy-MM}.mmdb.gz");
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var gzip = new GZipStream(body, CompressionMode.Decompress))
                await using (var file = File.Create(temp))
                {
                    await gzip.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
                File.Move(temp, path, overwrite: true);
                Open(path);
                _logger.LogInformation("GeoIP: loaded DB-IP's country database for {Month:yyyy-MM}.", month);
                return;
            }
            _logger.LogWarning("GeoIP: couldn't download DB-IP's country database; countries come from Cloudflare only.");
        }
        finally
        {
            _updating.Release();
        }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _updating.Dispose();
    }

    private void Open(string path)
    {
        if (!File.Exists(path))
            return;
        try
        {
            // Read into memory, so the file can be replaced while lookups go on.
            var previous = _reader;
            _reader = new Reader(path, FileAccessMode.Memory);
            previous?.Dispose();
        }
        catch (InvalidDatabaseException ex)
        {
            _logger.LogWarning(ex, "GeoIP: {Path} isn't a valid database; ignoring it.", path);
        }
    }
}

/// <summary>Keeps <see cref="IpCountry"/>'s database current (checked at start and daily).</summary>
public sealed class IpCountryUpdater : BackgroundService
{
    private readonly IpCountry _countries;
    private readonly TimeProvider _time;
    private readonly ILogger<IpCountryUpdater> _logger;

    public IpCountryUpdater(IpCountry countries, TimeProvider time, ILogger<IpCountryUpdater> logger)
    {
        _countries = countries;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _countries.UpdateAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "GeoIP: updating the country database failed; trying again tomorrow.");
            }
            await Task.Delay(TimeSpan.FromDays(1), _time, stoppingToken).ConfigureAwait(false);
        }
    }
}
