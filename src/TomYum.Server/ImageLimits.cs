using SkiaSharp;

namespace TomYum.Server;

/// <summary>
/// Decoding images people upload (beatmap backgrounds, medal icons, badges). A small file can hold a
/// huge picture (a 20000×20000 PNG is a few KB and 1.6 GB decoded), so the size in its header is read
/// first and anything bigger than <see cref="MaxSide"/> on a side is never decoded.
/// </summary>
public static class ImageLimits
{
    /// <summary>The widest and tallest image decoded.</summary>
    public const int MaxSide = 8000;

    /// <summary>The image's size from its header; null when it isn't an image.</summary>
    public static (int Width, int Height)? Size(SKCodec? codec) =>
        codec is { Info: { Width: > 0, Height: > 0 } info } ? (info.Width, info.Height) : null;

    /// <summary>The size of the image in <paramref name="bytes"/> (only its header is read); null when it isn't an image.</summary>
    public static (int Width, int Height)? Size(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        return Size(codec);
    }

    /// <summary>Whether the image is readable and no bigger than <see cref="MaxSide"/>×<see cref="MaxSide"/>.</summary>
    public static bool IsWithinLimit(SKCodec? codec) =>
        Size(codec) is { Width: <= MaxSide, Height: <= MaxSide };

    /// <summary>Whether <paramref name="bytes"/> are an image within the limit (only the header is read).</summary>
    public static bool IsWithinLimit(byte[] bytes) => Size(bytes) is { Width: <= MaxSide, Height: <= MaxSide };

    /// <summary>
    /// The image, decoded at <paramref name="scale"/> of its size or a little bigger where the format
    /// can (JPEG and WebP decode straight to a smaller size), else whole; null when it can't be read
    /// or is over the limit.
    /// </summary>
    public static SKBitmap? Decode(SKCodec? codec, double scale = 1)
    {
        if (codec is null || !IsWithinLimit(codec))
            return null;

        var info = codec.Info;
        if (info.AlphaType == SKAlphaType.Unpremul)
            info.AlphaType = SKAlphaType.Premul;
        if (scale is > 0 and < 1)
        {
            var scaled = codec.GetScaledDimensions((float)scale);
            if (scaled.Width >= Math.Ceiling(info.Width * scale) && scaled.Height >= Math.Ceiling(info.Height * scale)
                && (scaled.Width < info.Width || scaled.Height < info.Height))
                info = info.WithSize(scaled.Width, scaled.Height);
        }

        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
            return bitmap;
        bitmap.Dispose();
        return null;
    }

    /// <summary><see cref="Decode(SKCodec, double)"/> for bytes.</summary>
    public static SKBitmap? Decode(byte[] bytes, double scale = 1)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        return Decode(codec, scale);
    }
}
