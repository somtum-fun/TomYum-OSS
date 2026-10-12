using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using TomYum.Performance;
using TomYum.Server.Admin;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>Uploaded images: a huge picture in a small file is never decoded.</summary>
public sealed class ImageLimitsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tomyum-images-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void AHugePicture_IsSeenFromItsHeader_AndNotDecoded()
    {
        var bomb = PngClaiming(20_000, 20_000);

        Assert.True(bomb.Length < 1024);
        Assert.Equal((20_000, 20_000), ImageLimits.Size(bomb));
        Assert.False(ImageLimits.IsWithinLimit(bomb));
        Assert.Null(ImageLimits.Decode(bomb));
        Assert.Null(BadgeImages.Fit(bomb, 86, 40));
        Assert.Null(AssetsEndpoints.LegacyMedal(bomb));
        Assert.Null(AssetStore.Half(bomb, ".png"));
    }

    [Fact]
    public void Pictures_UpToTheLimit_AreDecoded()
    {
        Assert.True(ImageLimits.IsWithinLimit(Encode(100, 50, SKEncodedImageFormat.Png)));
        Assert.Null(ImageLimits.Size([1, 2, 3]));
        using var bitmap = ImageLimits.Decode(Encode(100, 50, SKEncodedImageFormat.Png));
        Assert.Equal((100, 50), (bitmap!.Width, bitmap.Height));
    }

    [Fact]
    public void Jpegs_AreDecodedOnlyAsBigAsNeeded()
    {
        var jpeg = Encode(1600, 800, SKEncodedImageFormat.Jpeg);

        using var scaled = ImageLimits.Decode(jpeg, 0.1);

        // Never smaller than asked (160×80), but far from the whole picture.
        Assert.InRange(scaled!.Width, 160, 400);
        Assert.InRange(scaled.Height, 80, 200);
    }

    [Fact]
    public async Task CoversOfAHugeBackground_AreNeverMade_AndTheFailureIsRemembered()
    {
        var store = Store();
        await store.SaveAsync(10_000_000, [1], (".png", PngClaiming(20_000, 20_000)), null);

        Assert.Null(store.CoverPath(10_000_000, "cover"));
        Assert.Null(store.CoverPath(10_000_000, "list"));
        Assert.False(Directory.Exists(Path.Combine(_root, "beatmaps", "covers", "10000000")));

        // A new background is tried again.
        await store.SaveAsync(10_000_000, [1], (".png", Encode(1920, 1080, SKEncodedImageFormat.Png)), null);
        using var cover = SKBitmap.Decode(store.CoverPath(10_000_000, "cover"));
        Assert.Equal((900, 250), (cover.Width, cover.Height));
    }

    [Fact]
    public async Task AnUpload_WithAHugeBackground_IsRefused()
    {
        var importer = new CustomBeatmapImporter(PackageNamesTests.NoBeatmaps(), new InMemoryUserRepository(), Store(),
            new OsuFileCache(Path.Combine(_root, "osu"), new HttpClient(), NullLogger<OsuFileCache>.Instance),
            TimeProvider.System, NullLogger<CustomBeatmapImporter>.Instance);
        var map = Encoding.UTF8.GetBytes(
            "osu file format v14\n\n[General]\nAudioFilename: audio.mp3\n\n[Metadata]\nTitle:Song\nArtist:Artist\nCreator:Mapper\nVersion:Hard\n\n[Events]\n0,0,\"bg.png\",0,0\n\n[HitObjects]\n64,192,1000,1,0,0:0:0:0:\n");

        var refused = await Assert.ThrowsAsync<CustomUploadException>(() => importer.ImportAsync(PackageNamesTests.Zip(("map.osu", map), ("bg.png", PngClaiming(9000, 100))), uploaderId: 3));

        Assert.Contains("9000×100", refused.Message, StringComparison.Ordinal);
    }

    private CustomBeatmapStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        [CustomBeatmapStore.PathKey] = Path.Combine(_root, "beatmaps"),
    }).Build());

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(new SKColor(40, 120, 200));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>A real 1×1 PNG whose header says it's <paramref name="width"/>×<paramref name="height"/> (what an image bomb looks like to a decoder).</summary>
    internal static byte[] PngClaiming(int width, int height)
    {
        var png = Encode(1, 1, SKEncodedImageFormat.Png);
        // Signature (8), then IHDR: length (4), "IHDR" (4), width, height, …, CRC over type and data.
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), Crc32(png.AsSpan(12, 17)));
        return png;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
