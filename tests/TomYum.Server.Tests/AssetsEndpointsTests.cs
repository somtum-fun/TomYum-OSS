using TomYum.Server.Beatmaps;

using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// Tests the medal-asset filename guard: only bare <c>*.png</c> filenames are served, so the cache
/// lookup can never be steered outside its directory (path traversal / absolute paths).
/// </summary>
public class AssetsEndpointsTests
{
    [Theory]
    [InlineData("osu-combo-500.png")]
    [InlineData("osu-combo-500@2x.png")]
    [InlineData("all-intro-suddendeath.png")]
    [InlineData("mania-skill-fc-8@2x.png")]
    public void Accepts_real_medal_icon_filenames(string file) =>
        Assert.True(AssetsEndpoints.IsSafeMedalFile(file));

    [Theory]
    [InlineData("../secret.png")]           // parent traversal
    [InlineData("..%2f..%2fetc%2fpasswd")]  // encoded traversal, no .png
    [InlineData("sub/dir/icon.png")]        // nested path
    [InlineData("/etc/passwd")]             // absolute path
    [InlineData("icon.jpg")]                // wrong extension
    [InlineData("icon.png.exe")]            // extension smuggling
    [InlineData("icon")]                    // no extension
    [InlineData("")]                        // empty
    [InlineData("osu-combo-500.png ")]      // trailing space
    public void Rejects_unsafe_or_non_png_filenames(string file) =>
        Assert.False(AssetsEndpoints.IsSafeMedalFile(file));
}
