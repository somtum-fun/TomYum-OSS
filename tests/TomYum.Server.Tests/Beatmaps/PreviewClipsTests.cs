using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

public sealed class PreviewClipsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tomyum-previews-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // No ffmpeg at this path: what the store does without one.
    private CustomBeatmapStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        [CustomBeatmapStore.PathKey] = _root,
        [PreviewClips.FfmpegKey] = Path.Combine(_root, "no-ffmpeg-here"),
    }).Build());

    private static byte[] FakeMp3() => [0xFF, 0xFB, .. new byte[4096]];

    [Fact]
    public async Task WithoutFfmpeg_AnMp3SongIsKeptWhole()
    {
        var store = Store();
        var song = FakeMp3();

        Assert.False(await store.SavePreviewAsync(5_000_001, new PreviewSong(song, ".mp3", 1000)));
        Assert.Equal(song, await File.ReadAllBytesAsync(store.PreviewPath(5_000_001)!));
    }

    [Fact]
    public async Task WithoutFfmpeg_AnOggSongHasNoPreview()
    {
        var store = Store();

        Assert.False(await store.SavePreviewAsync(5_000_002, new PreviewSong(FakeMp3(), ".ogg", 1000)));
        Assert.Null(store.PreviewPath(5_000_002));
    }

    [Fact]
    public async Task Backfill_ReadsTheSongAndPreviewPoint_AndStopsWithoutFfmpeg()
    {
        var store = Store();
        var osz = Osz(("Song.mp3", FakeMp3()), ("map.osu", Encoding.UTF8.GetBytes("osu file format v14\n\n[General]\nAudioFilename: song.mp3\nPreviewTime: 54321\n")));
        Directory.CreateDirectory(Path.GetDirectoryName(store.OszPath(5_000_003))!);
        await File.WriteAllBytesAsync(store.OszPath(5_000_003), osz);

        var song = CustomPreviewBackfill.ReadSong(store.OszPath(5_000_003));
        Assert.NotNull(song);
        Assert.Equal(54321, song.PreviewTime);
        Assert.Equal(".mp3", song.Extension);

        Assert.Equal([5_000_003], store.SetIds());
        Assert.Equal(0, await new CustomPreviewBackfill(store, NullLogger<CustomPreviewBackfill>.Instance).RunAsync());
    }

    private static byte[] Osz(params (string Name, byte[] Bytes)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in files)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(bytes);
            }
        }
        return memory.ToArray();
    }
}
