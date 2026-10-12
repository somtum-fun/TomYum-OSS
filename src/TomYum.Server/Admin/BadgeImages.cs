using System.Globalization;
using System.Net;
using System.Net.Sockets;
using SkiaSharp;
using TomYum.Server.Beatmaps;

namespace TomYum.Server.Admin;

/// <summary>
/// Profile badge images for the badge commands, kept in the asset cache's <c>profile-badges</c>
/// folder that <see cref="AssetsEndpoints"/> serves (and the website's badge images point to):
/// <list type="bullet">
/// <item>an osu! badge by file name (e.g. <c>contributor.png</c>): checked on osu!'s CDN and cached;</item>
/// <item>any image link (a Discord attachment, …): downloaded once and redrawn at badge size,
/// 86×40 and 172×80 for <c>@2x</c>, so it looks like the others and doesn't depend on the link;
/// saved under the name staff give (else the link's file name), never a made-up one.</item>
/// </list>
/// </summary>
public sealed class BadgeImages
{
    /// <summary>The download client: it only connects to public addresses.</summary>
    public const string HttpClientName = "BadgeImages";

    public const int Width = 86;
    public const int Height = 40;
    private const int MaxBytes = 8 * 1024 * 1024;

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _configuration;

    public BadgeImages(IHttpClientFactory http, IConfiguration configuration)
    {
        _http = http;
        _configuration = configuration;
    }

    /// <summary>
    /// The badge's image name for <paramref name="text"/> (a badge here or on osu! by its file name,
    /// or an image link), with both sizes ready to be served; or the reason it can't be used. A link's
    /// image is saved as <paramref name="name"/> (else the link's own file name), never a made-up one.
    /// </summary>
    public async Task<(string? Image, string? Error)> PrepareAsync(string text, string? name = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var link) && (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp))
            return await FromLinkAsync(link, name, cancellationToken).ConfigureAwait(false);

        var file = Path.HasExtension(text) ? text : text + ".png";
        if (!AssetsEndpoints.IsSafeMedalFile(file))
            return (null, "That isn't an osu! badge file name (like `contributor.png`) or an image link.");
        if (File.Exists(Path.Combine(Directory, file)))
            return (file, null);

        using var client = _http.CreateClient(AssetsEndpoints.HttpClientName);
        using var response = await client.GetAsync($"https://assets.ppy.sh/{AssetsEndpoints.BadgeFolder}/{file}", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (null, $"osu! has no badge called `{file}`. Give the file file from a badge's image link, or a link to your own image.");
        await SaveAsync(file, await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        // Its @2x too (osu!'s badges have one), so sharp screens don't ask for it later.
        using var retina = await client.GetAsync($"https://assets.ppy.sh/{AssetsEndpoints.BadgeFolder}/{AssetStore.RetinaPath(file)}", cancellationToken).ConfigureAwait(false);
        if (retina.IsSuccessStatusCode)
            await SaveAsync(AssetStore.RetinaPath(file), await retina.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return (file, null);
    }

    /// <summary>
    /// The file name a link's badge is saved as: the given name, else the link's own file name (both
    /// made plain: letters, digits, - and _), as .png. Null when there's nothing usable.
    /// </summary>
    public static string? BadgeFileName(string? name, Uri link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var raw = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(link.AbsolutePath)) : name.Trim();
        if (raw.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^4];
        // A link to the double one names the pair without it.
        if (raw.EndsWith("@2x", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^3];
        var plain = System.Text.RegularExpressions.Regex.Replace(raw.ToLowerInvariant().Replace(' ', '-'), "[^a-z0-9_-]", string.Empty).Trim('-', '_');
        return plain.Length is 0 or > 80 ? null : plain + ".png";
    }

    /// <summary>Redraws <paramref name="source"/> to fit <paramref name="width"/>×<paramref name="height"/> (centred, transparent around it).</summary>
    public static byte[]? Fit(byte[] source, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var codec = SKCodec.Create(new SKMemoryStream(source));
        if (ImageLimits.Size(codec) is not { } size)
            return null;
        // Never bigger than ImageLimits allows; decoded only as big as needed where the format can.
        using var original = ImageLimits.Decode(codec, Math.Min((double)width / size.Width, (double)height / size.Height));
        if (original is null || original.Width == 0 || original.Height == 0)
            return null;

        var scale = Math.Min((float)width / original.Width, (float)height / original.Height);
        var drawn = new SKRect(0, 0, original.Width * scale, original.Height * scale);
        drawn.Offset((width - drawn.Width) / 2, (height - drawn.Height) / 2);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(original);
        surface.Canvas.DrawImage(image, drawn, new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var snapshot = surface.Snapshot();
        using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    /// <summary>For the download client: connects only to public addresses (staff can't point it at the server's network).</summary>
    public static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(IsPublic)
            ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} isn't a public address.");
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Whether <paramref name="address"/> is on the internet (not loopback, private, link-local, …).</summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast);

        var b = address.GetAddressBytes();
        return !(b[0] is 0 or 10 or 127 or >= 224
                 || (b[0] == 100 && b[1] >= 64 && b[1] < 128)
                 || (b[0] == 169 && b[1] == 254)
                 || (b[0] == 172 && b[1] >= 16 && b[1] < 32)
                 || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 198 && b[1] is 18 or 19));
    }

    private string Directory => AssetsEndpoints.CacheDirectory(_configuration, AssetsEndpoints.BadgeFolder);

    private async Task<(string? Image, string? Error)> FromLinkAsync(Uri link, string? name, CancellationToken cancellationToken)
    {
        if (BadgeFileName(name, link) is not { } file)
            return (null, "Give the badge's image a name (letters, digits, - and _), like `ndc-2023`.");
        if (File.Exists(Path.Combine(Directory, file)))
            return (null, $"There's already a badge image called `{file}`: give another name, or give `{file}` itself to use that one.");

        byte[] data;
        try
        {
            using var client = _http.CreateClient(HttpClientName);
            using var response = await client.GetAsync(link, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, string.Create(CultureInfo.InvariantCulture, $"That link gave HTTP {(int)response.StatusCode}."));
            if (response.Content.Headers.ContentLength > MaxBytes)
                return (null, "That image is too big (8 MB at most).");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                    return (null, "That image is too big (8 MB at most).");
                buffer.Write(chunk, 0, read);
            }
            data = buffer.ToArray();
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Couldn't download that image: {ex.Message}");
        }

        if (Fit(data, Width, Height) is not { } normal || Fit(data, Width * 2, Height * 2) is not { } retina)
            return (null, "That link isn't an image (PNG, JPEG, GIF or WebP).");

        await SaveAsync(file, normal, cancellationToken).ConfigureAwait(false);
        await SaveAsync(AssetStore.RetinaPath(file), retina, cancellationToken).ConfigureAwait(false);
        return (file, null);
    }

    private async Task SaveAsync(string file, byte[] data, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, file);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, data, cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
