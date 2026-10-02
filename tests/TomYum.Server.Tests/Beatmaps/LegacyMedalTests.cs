using SkiaSharp;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>Today's medal icons at the size old clients draw them (docs/old-clients.md).</summary>
public class LegacyMedalTests
{
    private static byte[] Png(int width, int height, SKColor colour)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(colour);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void TodaysIcon_FitsTheOldSize_Centred()
    {
        var smaller = AssetsEndpoints.LegacyMedal(Png(193, 210, SKColors.Red));

        using var result = SKBitmap.Decode(smaller);
        Assert.Equal(AssetsEndpoints.LegacyMedalWidth, result.Width);
        Assert.Equal(AssetsEndpoints.LegacyMedalHeight, result.Height);
        // Taller than wide: the full height is used, transparent space left and right.
        Assert.Equal(SKColors.Red, result.GetPixel(result.Width / 2, result.Height / 2));
        Assert.Equal(0, result.GetPixel(2, result.Height / 2).Alpha);
    }

    [Fact]
    public void NotAnImage_IsNull() =>
        Assert.Null(AssetsEndpoints.LegacyMedal([1, 2, 3]));
}
