using System.Net;
using Microsoft.Extensions.Configuration;
using SkiaSharp;
using TomYum.Server.Admin;
using TomYum.Server.Beatmaps;
using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

public sealed class AssetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tomyum-assets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("profile-badges/ndc-2023.png", true)]
    [InlineData("tournament-banners/official/owc2025/profile/supporter_CA.jpg", true)]
    [InlineData("medals/client/all-skill-pass-1.png", true)]
    [InlineData("profile-badges/../tomyum.env", false)]
    [InlineData("profile-badges/.hidden.png", false)]
    [InlineData("replays/1.osr", false)]
    [InlineData("profile-badges/ndc 2023.png", false)]
    [InlineData("profile-badges/ndc-2023.txt", false)]
    [InlineData("profile-badges", false)]
    public void OnlyPlainImagePaths_InTheManagedFolders(string path, bool allowed) =>
        Assert.Equal(allowed, AssetStore.IsImagePath(path));

    [Fact]
    public async Task OnlyTheDouble_MakesTheNormalAtHalfSize()
    {
        var store = Store();

        Assert.Null(await store.SaveAsync("profile-badges/ndc-2023.png", null, Png(172, 80)));

        var pair = Assert.Single(store.List("profile-badges")!.Images);
        Assert.Equal("profile-badges/ndc-2023.png", pair.Path);
        Assert.Equal((86, 40), (pair.Normal!.Width, pair.Normal.Height));
        Assert.Equal((172, 80), (pair.Retina!.Width, pair.Retina.Height));
    }

    [Fact]
    public async Task FoldersAndPairs_AreListed_ANormalWithoutItsDoubleToo()
    {
        var store = Store();
        await store.SaveAsync("tournament-banners/official/owc2025/profile/supporter_CA.jpg", Jpeg(1000, 60), Jpeg(2000, 120));
        await store.SaveAsync("tournament-banners/official/owc2025/profile/winner_CA.jpg", Jpeg(1000, 60), null);

        Assert.Equal(["official"], store.List("tournament-banners")!.Folders);
        var images = store.List("tournament-banners/official/owc2025/profile")!.Images;
        Assert.Equal(["supporter_CA.jpg", "winner_CA.jpg"], images.Select(i => i.Name));
        Assert.Null(images[1].Retina);
        Assert.Null(store.List("replays"));
    }

    [Fact]
    public async Task BannersAreJpeg_AndNamesArePlain()
    {
        var store = Store();

        Assert.NotNull(await store.SaveAsync("tournament-banners/x/banner.png", Png(10, 10), null));
        Assert.NotNull(await store.SaveAsync("profile-badges/ndc@2x.png", Png(10, 10), null));
        Assert.NotNull(await store.SaveAsync("profile-badges/ndc.png", [1, 2, 3], null));
    }

    [Fact]
    public async Task RenamingMovesBothSizes_ButNeverOverAnother()
    {
        var store = Store();
        await store.SaveAsync("profile-badges/tomyum-0a9a48d63b433156.png", Png(86, 40), Png(172, 80));
        await store.SaveAsync("profile-badges/taken.png", Png(86, 40), null);

        Assert.NotNull(store.Rename("profile-badges/tomyum-0a9a48d63b433156.png", "taken"));
        Assert.Null(store.Rename("profile-badges/tomyum-0a9a48d63b433156.png", "server-tester"));

        var pair = store.Pair("profile-badges/server-tester.png");
        Assert.NotNull(pair.Normal);
        Assert.NotNull(pair.Retina);
        Assert.False(File.Exists(Path.Combine(_root, "profile-badges", "tomyum-0a9a48d63b433156.png")));
    }

    [Fact]
    public async Task CopiedFromOsu_BothSizes_AtTheSamePath()
    {
        var asked = new List<string>();
        var store = Store(url =>
        {
            asked.Add(url);
            return url.EndsWith("@2x.jpg", StringComparison.Ordinal) ? Jpeg(2000, 120) : Jpeg(1000, 60);
        });

        var (path, error) = await store.ImportAsync("https://assets.ppy.sh/tournament-banners/official/owc2025/profile/supporter_CA@2x.jpg");

        Assert.Null(error);
        Assert.Equal("tournament-banners/official/owc2025/profile/supporter_CA.jpg", path);
        Assert.Equal(
            ["https://assets.ppy.sh/tournament-banners/official/owc2025/profile/supporter_CA.jpg", "https://assets.ppy.sh/tournament-banners/official/owc2025/profile/supporter_CA@2x.jpg"],
            asked);
        Assert.NotNull(store.Pair(path!).Retina);
        Assert.NotNull((await store.ImportAsync("https://example.com/not-osu.png")).Error);
    }

    [Theory]
    [InlineData("NDC 2023", "https://cdn.example/x.png", "ndc-2023.png")]
    [InlineData(null, "https://cdn.discordapp.com/attachments/1/2/Server_Tester.png?ex=1", "server_tester.png")]
    [InlineData("", "https://cdn.example/badge@2x.png", "badge.png")]
    [InlineData("!!!", "https://cdn.example/x.png", null)]
    public void ALinksBadge_IsSavedUnderAGivenName_NeverAMadeUpOne(string? name, string link, string? expected) =>
        Assert.Equal(expected, BadgeImages.BadgeFileName(name, new Uri(link)));

    private AssetStore Store(Func<string, byte[]?>? osu = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assets:CachePath"] = _root }).Build(), new FakeHttp(osu ?? (_ => null)));

    private static byte[] Png(int width, int height) => Encode(width, height, SKEncodedImageFormat.Png);

    private static byte[] Jpeg(int width, int height) => Encode(width, height, SKEncodedImageFormat.Jpeg);

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.HotPink);
        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    private sealed class FakeHttp(Func<string, byte[]?> answer) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request.RequestUri!.ToString()) is { } bytes
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
