using SkiaSharp;

namespace TomYum.Server.Discord;

/// <summary>
/// A player's rank over the last 90 days as a PNG, like the profile's rank graph: better ranks are
/// higher, days without a rank are left out. No text in the image (the server has no fonts): the
/// numbers go in the message around it.
/// </summary>
public static class RankGraph
{
    public const int Width = 900;
    public const int Height = 300;

    private const float Padding = 24;

    /// <summary>The graph, or null when fewer than two days have a rank.</summary>
    public static byte[]? Render(IReadOnlyList<int> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        var ranked = days.Select((rank, day) => (Rank: rank, Day: day)).Where(d => d.Rank > 0).ToList();
        if (ranked.Count < 2)
            return null;

        var best = ranked.Min(d => d.Rank);
        var worst = ranked.Max(d => d.Rank);
        // A flat line still needs some height to sit in the middle of.
        var span = Math.Max(worst - best, 1);
        float X(int day) => Padding + day * (Width - 2 * Padding) / Math.Max(days.Count - 1, 1);
        float Y(int rank) => Padding + (float)(rank - best) / span * (Height - 2 * Padding);

        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x2A, 0x22, 0x26));

        using (var grid = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0x18), StrokeWidth = 1, IsAntialias = true })
        {
            for (var i = 0; i <= 4; i++)
            {
                var y = Padding + i * (Height - 2 * Padding) / 4;
                canvas.DrawLine(Padding, y, Width - Padding, y, grid);
            }
        }

        // The line, broken where days have no rank.
        using var line = new SKPaint { Color = new SKColor(0xFF, 0xCC, 0x22), StrokeWidth = 4, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        for (var i = 1; i < ranked.Count; i++)
        {
            var (fromRank, fromDay) = ranked[i - 1];
            var (toRank, toDay) = ranked[i];
            if (toDay == fromDay + 1)
                canvas.DrawLine(X(fromDay), Y(fromRank), X(toDay), Y(toRank), line);
        }

        // The best day and today.
        using var dot = new SKPaint { Color = new SKColor(0xFF, 0x66, 0xAA), IsAntialias = true };
        var peak = ranked.First(d => d.Rank == best);
        canvas.DrawCircle(X(peak.Day), Y(peak.Rank), 7, dot);
        var last = ranked[^1];
        dot.Color = SKColors.White;
        canvas.DrawCircle(X(last.Day), Y(last.Rank), 6, dot);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
