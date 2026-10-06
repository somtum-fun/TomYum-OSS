using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TomYum.Server.Beatmaps;

using Xunit;

namespace TomYum.Server.Tests.Beatmaps;

/// <summary>
/// The main-menu banners: kept as osu!'s menu-content.json (lazer and osu-web both read it, and
/// osu-web needs every key, null or not), added and removed by staff.
/// </summary>
public sealed class MenuBannersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tomyum-menu-banners-" + Guid.NewGuid().ToString("N"));
    private readonly MenuBanners _banners;

    public MenuBannersTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Assets:CachePath"] = _root })
            .Build();
        _banners = new MenuBanners(configuration);
    }

    public void Dispose()
    {
        _banners.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task No_file_is_an_empty_list()
    {
        Assert.Empty(await _banners.ListAsync());
        using var content = JsonDocument.Parse(await _banners.ContentAsync());
        Assert.Equal(0, content.RootElement.GetProperty("images").GetArrayLength());
    }

    [Fact]
    public async Task Added_banners_are_written_as_osus_menu_content()
    {
        var expires = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Null(await _banners.AddAsync("https://assets.example/a.png", "https://example/a", null, expires));
        Assert.Null(await _banners.AddAsync("https://assets.example/b.png", "https://example/b", null, null));

        using var content = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, MenuBanners.FileName)));
        var images = content.RootElement.GetProperty("images");
        Assert.Equal(2, images.GetArrayLength());
        var first = images[0];
        Assert.Equal("https://assets.example/a.png", first.GetProperty("image").GetString());
        Assert.Equal("https://example/a", first.GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("begins").ValueKind);
        Assert.Equal(expires, first.GetProperty("expires").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("not a link", "https://example/a")]
    [InlineData("https://assets.example/a.png", "")]
    [InlineData("javascript:alert(1)", "https://example/a")]
    public async Task Rejects_what_isnt_a_web_address(string image, string url)
    {
        Assert.NotNull(await _banners.AddAsync(image, url, null, null));
        Assert.Empty(await _banners.ListAsync());
    }

    [Fact]
    public async Task Rejects_the_same_banner_twice()
    {
        Assert.Null(await _banners.AddAsync("https://assets.example/a.png", "https://example/a", null, null));
        Assert.NotNull(await _banners.AddAsync("https://assets.example/a.png", "https://example/a", null, null));
        Assert.Single(await _banners.ListAsync());
    }

    [Fact]
    public async Task Removes_by_place_in_the_list()
    {
        await _banners.AddAsync("https://assets.example/a.png", "https://example/a", null, null);
        await _banners.AddAsync("https://assets.example/b.png", "https://example/b", null, null);

        Assert.Null(await _banners.RemoveAsync(2));
        Assert.Equal("https://assets.example/a.png", (await _banners.RemoveAsync(0))?.Image);
        Assert.Equal("https://assets.example/b.png", Assert.Single(await _banners.ListAsync()).Image);
    }

    [Fact]
    public void Current_means_started_and_not_ended()
    {
        var now = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        Assert.True(MenuBanners.IsCurrent(new MenuBanner("i", "u", null, null), now));
        Assert.True(MenuBanners.IsCurrent(new MenuBanner("i", "u", now.AddDays(-1), now.AddDays(1)), now));
        Assert.False(MenuBanners.IsCurrent(new MenuBanner("i", "u", now.AddDays(1), null), now));
        Assert.False(MenuBanners.IsCurrent(new MenuBanner("i", "u", null, now), now));
    }
}
