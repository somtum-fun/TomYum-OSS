using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary><see cref="PackArchives"/>: a pack's zip of .osz files, built once and kept.</summary>
public sealed class PackArchivesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tomyum-packs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Start_BuildsOneZipOfTheSets_ThenServesIt()
    {
        var opened = new List<int>();
        using var archives = New((id, _) =>
        {
            opened.Add(id);
            return Task.FromResult<(Stream, string?)>((new MemoryStream(Encoding.UTF8.GetBytes("osz " + id)), id == 1 ? "1 Artist - Title.osz" : null));
        });

        Assert.Equal("none", archives.Status("S1", [1, 2]).State);
        archives.Start("S1", [1, 2]);
        var status = await WaitAsync(archives, "S1", [1, 2]);

        Assert.Equal("ready", status.State);
        var path = archives.ReadyPath("S1", [2, 1]);
        Assert.NotNull(path);
        using (var zip = ZipFile.OpenRead(path!))
            Assert.Equal(["1 Artist - Title.osz", "2.osz"], zip.Entries.Select(e => e.FullName).Order().ToArray());

        // Ready: nothing is downloaded again.
        archives.Start("S1", [1, 2]);
        Assert.Equal(2, opened.Count);
    }

    [Fact]
    public async Task ChangedSets_BuildANewZip_AndTheOldOneGoes()
    {
        using var archives = New((id, _) => Task.FromResult<(Stream, string?)>((new MemoryStream([1, 2, 3]), null)));
        archives.Start("T3", [1]);
        await WaitAsync(archives, "T3", [1]);
        var first = archives.ReadyPath("T3", [1]);

        archives.Start("T3", [1, 5]);
        await WaitAsync(archives, "T3", [1, 5]);

        Assert.NotNull(archives.ReadyPath("T3", [1, 5]));
        Assert.False(File.Exists(first));
    }

    [Fact]
    public async Task AMissingSet_FailsTheBuild_AndItCanBeTriedAgain()
    {
        var fail = true;
        using var archives = New((id, _) => fail && id == 2
            ? throw new InvalidOperationException("The mirror doesn't have set 2.")
            : Task.FromResult<(Stream, string?)>((new MemoryStream([1]), null)));

        archives.Start("A1", [1, 2]);
        var status = await WaitAsync(archives, "A1", [1, 2]);
        Assert.Equal("failed", status.State);
        Assert.Contains("set 2", status.Error, StringComparison.Ordinal);
        Assert.Null(archives.ReadyPath("A1", [1, 2]));

        fail = false;
        archives.Start("A1", [1, 2]);
        Assert.Equal("ready", (await WaitAsync(archives, "A1", [1, 2])).State);
    }

    [Fact]
    public async Task AFailedBuild_WaitsBeforeAPlayerStartsItAgain()
    {
        var opened = 0;
        using var archives = New((_, _) =>
        {
            opened++;
            throw new InvalidOperationException("The mirror doesn't have it.");
        });

        archives.Start("B1", [1]);
        await WaitAsync(archives, "B1", [1]);

        Assert.Equal("failed", archives.Start("B1", [1], PackArchives.FailedRetryDelay).State);
        Assert.Equal(1, opened);

        archives.Start("B1", [1]);
        await WaitAsync(archives, "B1", [1]);
        Assert.Equal(2, opened);
    }

    [Theory]
    [InlineData("https://osu.example.com/d/packs/S1", "S1", true)]
    [InlineData("https://osu.example.com/d/packs/s1", "S1", true)]
    [InlineData("https://example.com/pack.zip", "S1", false)]
    [InlineData("https://osu.example.com/d/packs/S12", "S1", false)]
    [InlineData("", "S1", false)]
    public void OnlyPacksLinkingHere_AreBuilt(string url, string tag, bool expected) =>
        Assert.Equal(expected, PackEndpoints.IsBuiltHere(url, tag));

    [Theory]
    [InlineData("S1", true)]
    [InlineData("SM331", true)]
    [InlineData("../x", false)]
    [InlineData("TOOLONG", false)]
    [InlineData("", false)]
    public void Tags_AreShortLettersAndDigits(string tag, bool valid) => Assert.Equal(valid, PackArchives.IsValidTag(tag));

    [Theory]
    [InlineData(5, null, "5.osz")]
    [InlineData(5, "5 A - B.osz", "5 A - B.osz")]
    [InlineData(5, "../evil:name", "evilname.osz")]
    public void EntryNames_AreSafe(int setId, string? name, string expected) => Assert.Equal(expected, PackArchives.EntryName(setId, name));

    [Fact]
    public void DownloadNames_NameThePack() => Assert.Equal("S1 - Pack one.zip", PackEndpoints.DownloadName("S1", "Pack: one"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private PackArchives New(Func<int, CancellationToken, Task<(Stream Body, string? Name)>> open) =>
        new(
            Options.Create(new PackArchiveOptions { Path = _root, DelayBetweenSets = TimeSpan.Zero }),
            Options.Create(new DirectOptions()),
            new CustomBeatmapStore(new ConfigurationBuilder().Build()),
            null!,
            NullLogger<PackArchives>.Instance)
        {
            OpenSet = open,
        };

    private static async Task<PackArchiveStatus> WaitAsync(PackArchives archives, string tag, int[] sets)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = archives.Status(tag, sets);
            if (status.State is "ready" or "failed")
                return status;
            await Task.Delay(25);
        }
        throw new TimeoutException("The pack didn't build.");
    }
}
