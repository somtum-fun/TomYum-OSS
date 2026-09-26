using TomYum.Server.Bancho;
using Xunit;

namespace TomYum.Server.Tests;

/// <summary>
/// The GET / bancho status page loads from its embedded resource and injects the version.
/// </summary>
public class BanchoBoatPageTests
{
    [Fact]
    public void Render_returns_self_contained_boat_html_with_version()
    {
        var html = BanchoBoatPage.Render("9.9.9-test");

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("TomYum bancho 9.9.9-test", html);   // version injected into the status badge
        Assert.Contains("class=\"boat-track\"", html);        // the sailing-animation container
        Assert.Contains("@keyframes sail", html);             // the boat actually moves
        Assert.Contains("somtum-fun/TomYum-OSS", html);           // credit / repo link
        Assert.Contains("blueskychan_", html);                // author credit
        Assert.DoesNotContain("ppy Pty Ltd", html);           // ppy attribution removed
        Assert.DoesNotContain("banchoboat", html);            // banchoboat link removed
        Assert.DoesNotContain("{{VERSION}}", html);           // placeholder fully replaced
    }
}
