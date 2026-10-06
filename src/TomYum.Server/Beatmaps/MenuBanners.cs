using System.Text.Json;
using System.Text.Json.Serialization;

namespace TomYum.Server.Beatmaps;

/// <summary>A banner on lazer's main menu: its image, where clicking it goes, and when it shows.</summary>
public sealed record MenuBanner(
    [property: JsonPropertyName("image")] string Image,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("begins")] DateTimeOffset? Begins,
    [property: JsonPropertyName("expires")] DateTimeOffset? Expires);

/// <summary>
/// The main menu's banners, kept as osu!'s <c>menu-content.json</c> in the asset cache root and served
/// at <c>assets.&lt;domain&gt;/menu-content.json</c>: lazer (whose <c>assets.ppy.sh</c> the devserver
/// patch points here) rotates through the current ones, and the website shows them on its home page
/// (osu-web's <c>OSU_URL_MENU_CONTENT_JSON</c>). Changed with Discord's /banner_add and /banner_remove
/// and the admin panel's Menu banners page.
/// </summary>
public sealed class MenuBanners : IDisposable
{
    public const string FileName = "menu-content.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MenuBanners(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _path = Path.Combine(AssetsEndpoints.CacheDirectory(configuration, string.Empty), FileName);
    }

    public void Dispose() => _lock.Dispose();

    /// <summary>The file as clients read it (<c>{"images": []}</c> when there are none).</summary>
    public async Task<string> ContentAsync(CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(new Content(await ListAsync(cancellationToken).ConfigureAwait(false)), Json);

    /// <summary>Every banner, in the order the menu shows them (expired ones too).</summary>
    public async Task<IReadOnlyList<MenuBanner>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Adds a banner at the end; the problem, if any.</summary>
    public async Task<string?> AddAsync(string image, string url, DateTimeOffset? begins, DateTimeOffset? expires, CancellationToken cancellationToken = default)
    {
        image = image?.Trim() ?? string.Empty;
        url = url?.Trim() ?? string.Empty;
        if (!IsWebAddress(image))
            return "The image should be a web address (https://…).";
        if (!IsWebAddress(url))
            return "The click link should be a web address (https://…).";
        if (begins is { } from && expires is { } to && to <= from)
            return "It should end after it starts.";

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var banners = (await ReadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (banners.Any(b => b.Image == image && b.Url == url))
                return "That banner is already on the menu.";
            banners.Add(new MenuBanner(image, url, begins, expires));
            await WriteAsync(banners, cancellationToken).ConfigureAwait(false);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Removes the banner at <paramref name="index"/> (0 = first); the one removed, or null.</summary>
    public async Task<MenuBanner?> RemoveAsync(int index, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var banners = (await ReadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (index < 0 || index >= banners.Count)
                return null;
            var removed = banners[index];
            banners.RemoveAt(index);
            await WriteAsync(banners, cancellationToken).ConfigureAwait(false);
            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Whether a banner shows now (no start yet reached, or end passed, hides it).</summary>
    public static bool IsCurrent(MenuBanner banner, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(banner);
        return (banner.Begins is not { } begins || begins <= now) && (banner.Expires is not { } expires || expires > now);
    }

    private static bool IsWebAddress(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private async Task<IReadOnlyList<MenuBanner>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return [];
        await using var stream = File.OpenRead(_path);
        var content = await JsonSerializer.DeserializeAsync<Content>(stream, Json, cancellationToken).ConfigureAwait(false);
        return content?.Images ?? [];
    }

    private async Task WriteAsync(IReadOnlyList<MenuBanner> banners, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        // Written beside it and moved over, so a request never reads half a file.
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Content(banners), Json), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _path, overwrite: true);
    }

    private sealed record Content([property: JsonPropertyName("images")] IReadOnlyList<MenuBanner> Images);
}
