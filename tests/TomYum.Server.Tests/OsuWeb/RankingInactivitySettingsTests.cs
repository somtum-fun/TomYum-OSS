using TomYum.Server.OsuWeb;
using Xunit;

namespace TomYum.Server.Tests.OsuWeb;

/// <summary>The inactivity rule from the admin panel's two settings and the startup value.</summary>
public sealed class RankingInactivitySettingsTests
{
    [Theory]
    [InlineData(null, null, 180, 180)]       // nothing stored: the startup value
    [InlineData("30", "remove", 180, 30)]    // the panel's days
    [InlineData("0", "remove", 180, 0)]      // the panel turned it off
    [InlineData("30", "grey", 180, 0)]       // grey: inactive players stay ranked, so nobody is taken off
    [InlineData(" 45 ", " Remove ", 0, 45)]  // as typed
    [InlineData("abc", "remove", 90, 90)]    // not a number: the startup value
    [InlineData("-5", "remove", 90, 90)]
    public void TheRuleComesFromThePanelThenTheStartupValue(string? days, string? mode, int configured, int expected) =>
        Assert.Equal(expected, RankingInactivitySettings.Effective(days, mode, configured));
}
