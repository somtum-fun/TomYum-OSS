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

    [Fact]
    public async Task Backfill_LeavesSongsThatArentAudioAlone()
    {
        var store = Store();
        var playlist = Encoding.UTF8.GetBytes("#EXTM3U\nhttp://169.254.169.254/latest/meta-data/\n");
        var osz = Osz(("song.m3u8", playlist), ("map.osu", Encoding.UTF8.GetBytes("osu file format v14\n\n[General]\nAudioFilename: song.m3u8\n")));
        Directory.CreateDirectory(Path.GetDirectoryName(store.OszPath(5_000_004))!);
        await File.WriteAllBytesAsync(store.OszPath(5_000_004), osz);

        Assert.Null(CustomPreviewBackfill.ReadSong(store.OszPath(5_000_004)));
    }

    [Theory]
    [InlineData(".m3u8")]
    [InlineData(".m3u")]
    [InlineData(".txt")]
    [InlineData(".flac")]
    [InlineData("")]
    public async Task OnlyMp3OggAndWav_ReachFfmpeg(string extension)
    {
        var (ffmpeg, log) = RecordingFfmpeg();

        Assert.Null(await PreviewClips.MakeAsync(FakeMp3(), extension, 1000, ffmpeg));
        Assert.False(File.Exists(log));
        Assert.Null(PreviewClips.InputFormat(extension));
    }

    [Theory]
    [InlineData(".mp3", "mp3")]
    [InlineData(".OGG", "ogg")]
    [InlineData("wav", "wav")]
    public async Task Ffmpeg_ReadsLocalFilesOnly_AsTheSongsFormat(string extension, string format)
    {
        if (OperatingSystem.IsWindows())
            return;
        var (ffmpeg, log) = RecordingFfmpeg();

        await PreviewClips.MakeAsync(FakeMp3(), extension, 1000, ffmpeg);

        // Both runs (the length, then the clip) name the format and allow nothing but files.
        var runs = await File.ReadAllLinesAsync(log);
        Assert.Equal(2, runs.Length);
        Assert.All(runs, run => Assert.Contains($" -protocol_whitelist file -f {format} -i ", run, StringComparison.Ordinal));
    }

    // A stand-in ffmpeg that writes down its arguments (one run per line) and says the song is 10 s long.
    private (string Ffmpeg, string Log) RecordingFfmpeg()
    {
        Directory.CreateDirectory(_root);
        var log = Path.Combine(_root, "ffmpeg.log");
        var script = Path.Combine(_root, "ffmpeg.sh");
        File.WriteAllText(script, $"#!/bin/sh\necho \" $*\" >> '{log}'\necho 'Duration: 00:00:10.00, start' >&2\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (script, log);
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

public sealed class DirectOsuFilesTests
{
    [Theory]
    [InlineData("osu file format v14\n[General]", true)]
    [InlineData("﻿osu file format v14\n[General]", true)]
    [InlineData("<html><body>not found</body></html>", false)]
    [InlineData("", false)]
    public void OnlyRealOsuFilesAreKept(string text, bool kept) =>
        Assert.Equal(kept, CustomBeatmapCatalog.IsOsuFile(System.Text.Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void MirrorUrlIsCatboys() =>
        Assert.Equal("https://catboy.best/osu/123", CustomBeatmapCatalog.MirrorOsuUrl(123));
}

public sealed class OsuFileBpmTests
{
    private static OsuFileText File(string timingPoints, string hitObjects) => OsuFileText.Parse(System.Text.Encoding.UTF8.GetBytes(
        "osu file format v14\n\n[General]\nMode: 0\n\n[TimingPoints]\n" + timingPoints + "\n\n[HitObjects]\n" + hitObjects + "\n"));

    [Fact]
    public void TheBpmItsAtTheLongest_NotTheFirst()
    {
        // A 154 BPM intro (10 s), then 200 BPM until the last object at 200 s; a green line in between doesn't count.
        var file = File("0,389.61,4,2,1,60,1,0\n10000,300,4,2,1,60,1,0\n50000,-50,4,2,1,60,0,0", "256,192,200000,1,0,0:0:0:0:");
        Assert.Equal(200, file.Bpm);
    }

    [Fact]
    public void OneTimingPoint()
    {
        Assert.Equal(180, File("500,333.333333,4,2,1,60,1,0", "256,192,90000,1,0,0:0:0:0:").Bpm);
    }

    [Fact]
    public void NoTimingPoints() => Assert.Null(File(string.Empty, "256,192,1000,1,0,0:0:0:0:").Bpm);
}
