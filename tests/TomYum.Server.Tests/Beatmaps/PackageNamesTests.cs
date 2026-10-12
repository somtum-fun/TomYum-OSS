using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TomYum.Domain.Repositories;
using TomYum.Performance;
using TomYum.Server.Bancho;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>File names inside uploaded packages: nothing a client would unpack outside the set's folder.</summary>
public sealed class PackageNamesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tomyum-names-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("map.osu", "map.osu")]
    [InlineData("sb\\fx\\star.png", "sb/fx/star.png")]
    [InlineData("SB//a.png", "SB/a.png")]
    [InlineData("folder/", "folder")]
    [InlineData("a..b.png", "a..b.png")]
    [InlineData(".hidden", ".hidden")]
    public void PlainNames_AreAllowed(string name, string canonical) => Assert.Equal(canonical, PackageNames.Canonical(name));

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/etc/passwd")]
    [InlineData("\\Windows\\win.ini")]
    [InlineData("../evil.dll")]
    [InlineData("..\\..\\osu!.exe")]
    [InlineData("sb/../../evil.dll")]
    [InlineData("./map.osu")]
    [InlineData("sb/.../x.png")]
    [InlineData("sb/.. /x.png")]
    [InlineData("C:\\evil.dll")]
    [InlineData("C:evil.dll")]
    [InlineData("map.osu:stream")]
    [InlineData("a\0b.png")]
    public void ClimbingRootedOrDriveNames_AreRefused(string name) => Assert.False(PackageNames.IsSafe(name));

    [Theory]
    [InlineData("../../evil.dll")]
    [InlineData("C:\\Users\\evil.dll")]
    [InlineData("/tmp/evil.dll")]
    public async Task AnUpload_WithAnUnsafeName_IsRefused(string name)
    {
        var importer = new CustomBeatmapImporter(NoBeatmaps(), new InMemoryUserRepository(), Store(),
            new OsuFileCache(Path.Combine(_root, "osu"), new HttpClient(), NullLogger<OsuFileCache>.Instance),
            TimeProvider.System, NullLogger<CustomBeatmapImporter>.Instance);

        var refused = await Assert.ThrowsAsync<CustomUploadException>(() => importer.ImportAsync(Zip(("map.osu", Map()), (name, [1, 2, 3])), uploaderId: 3));
        Assert.Contains("isn't allowed", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Store().SetIds());
    }

    [Fact]
    public async Task StoredSets_WithUnsafeNames_AreListed()
    {
        var store = Store();
        Directory.CreateDirectory(Path.GetDirectoryName(store.OszPath(10_000_000))!);
        await File.WriteAllBytesAsync(store.OszPath(10_000_000), Zip(("map.osu", Map()), ("sb/star.png", [1])));
        await File.WriteAllBytesAsync(store.OszPath(10_000_001), Zip(("map.osu", Map()), ("..\\..\\evil.dll", [1]), ("C:/x.png", [1])));
        await File.WriteAllBytesAsync(store.OszPath(10_000_002), [1, 2, 3]);

        var found = PackageNames.FindUnsafeSets(store);

        Assert.Equal([10_000_001, 10_000_002], found.Select(f => f.SetId));
        Assert.Equal(["..\\..\\evil.dll", "C:/x.png"], found[0].Names);
        Assert.StartsWith("(couldn't read the file", found[1].Names[0], StringComparison.Ordinal);
    }

    private CustomBeatmapStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        [CustomBeatmapStore.PathKey] = Path.Combine(_root, "beatmaps"),
    }).Build());

    private static byte[] Map() => Encoding.UTF8.GetBytes(
        "osu file format v14\n\n[General]\nAudioFilename: audio.mp3\nMode: 0\n\n[Metadata]\nTitle:Song\nArtist:Artist\nCreator:Mapper\nVersion:Hard\n\n[HitObjects]\n64,192,1000,1,0,0:0:0:0:\n");

    internal static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }
        return output.ToArray();
    }

    // The package is refused before the importer looks anything up.
    internal static IBeatmapRepository NoBeatmaps() => DispatchProxy.Create<IBeatmapRepository, Unreachable>();

    public class Unreachable : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException($"{targetMethod?.Name} wasn't expected.");
    }
}
