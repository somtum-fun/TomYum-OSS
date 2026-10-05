using System.Diagnostics;
using System.Globalization;

namespace TomYum.Server.Beatmaps;

/// <summary>
/// Custom sets' audio previews, made from their songs as osu!'s are: 30 seconds from the map's preview
/// point (40% in without one, as lazer does), fading in over a second and out over its last 2.5, as
/// 128 kbps MP3. Made with ffmpeg (on the server's image; <c>Previews:Ffmpeg</c> names another one).
/// </summary>
public static class PreviewClips
{
    public const string FfmpegKey = "Previews:Ffmpeg";

    public const double LengthSeconds = 30;
    public const double FadeInSeconds = 1;
    public const double FadeOutSeconds = 2.5;

    /// <summary>A preview no bigger than this was made here (30 s at 128 kbps is under 500 KB); bigger is a whole song.</summary>
    public const long MaxClipBytes = 1_000_000;

    /// <summary>The preview of <paramref name="song"/> (any audio ffmpeg reads), or null when ffmpeg can't make one.</summary>
    public static async Task<byte[]?> MakeAsync(byte[] song, string extension, int previewTimeMs, string ffmpeg = "ffmpeg", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        var directory = Path.Combine(Path.GetTempPath(), "tomyum-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "song" + (extension.StartsWith('.') ? extension : "." + extension));
            var output = Path.Combine(directory, "preview.mp3");
            await File.WriteAllBytesAsync(input, song, cancellationToken).ConfigureAwait(false);

            if (await DurationAsync(ffmpeg, input, cancellationToken).ConfigureAwait(false) is not { } duration || duration <= 0)
                return null;

            var start = previewTimeMs >= 0 && previewTimeMs / 1000.0 < duration ? previewTimeMs / 1000.0 : duration * 0.4;
            var length = Math.Min(LengthSeconds, duration - start);
            var fadeOutStart = Math.Max(0, length - FadeOutSeconds);

            var arguments = new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-ss", Seconds(start), "-t", Seconds(length), "-i", input,
                "-vn", "-map_metadata", "-1",
                "-af", string.Create(CultureInfo.InvariantCulture, $"afade=t=in:st=0:d={FadeInSeconds},afade=t=out:st={fadeOutStart:0.###}:d={Math.Min(FadeOutSeconds, length):0.###}"),
                "-ac", "2", "-ar", "44100", "-b:a", "128k", "-f", "mp3", output,
            };

            var (exitCode, _) = await RunAsync(ffmpeg, arguments, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0 || !File.Exists(output))
                return null;

            var clip = await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
            return clip.Length > 0 ? clip : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // No ffmpeg here (tests, a bare machine): the caller keeps what it had.
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // The song's length in seconds, from ffmpeg's own report (ffprobe isn't always beside it).
    private static async Task<double?> DurationAsync(string ffmpeg, string input, CancellationToken cancellationToken)
    {
        var (_, output) = await RunAsync(ffmpeg, ["-hide_banner", "-i", input], cancellationToken).ConfigureAwait(false);
        var marker = output.IndexOf("Duration: ", StringComparison.Ordinal);
        if (marker < 0)
            return null;
        var text = output.Substring(marker + "Duration: ".Length, 11);
        return TimeSpan.TryParseExact(text, @"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture, out var duration) ? duration.TotalSeconds : null;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg didn't start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));

        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("ffmpeg took too long.");
        }
        return (process.ExitCode, await error.ConfigureAwait(false) + await output.ConfigureAwait(false));
    }

    private static string Seconds(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
}
