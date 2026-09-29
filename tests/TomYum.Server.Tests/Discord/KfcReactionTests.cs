using TomYum.Server.Discord;
using Xunit;

namespace TomYum.Server.Tests.Discord;

/// <summary>The bot's reaction to "kfc".</summary>
public sealed class KfcReactionTests
{
    [Theory]
    [InlineData("kfc", true)]
    [InlineData("KFC time", true)]
    [InlineData("who wants Kfc?", true)]
    [InlineData("<Player> kfc!!", true)]           // relayed from the game
    [InlineData("kfcs", false)]
    [InlineData("xkfc", false)]
    [InlineData("k f c", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Kfc_IsAWordOfItsOwn(string? text, bool reacts) => Assert.Equal(reacts, DiscordBotService.SaysKfc(text));
}
