using FluentAssertions;
using SilverModManager.Games.Bethesda.LoadOrder.Formats;

namespace SilverModManager.Games.Bethesda.LoadOrder.Tests.Formats;

public class OblivionIniTests
{
    [Theory]
    [InlineData("[General]\nbUseMyGamesDirectory=0\n", false)]
    [InlineData("[General]\r\nbUseMyGamesDirectory=0\r\n", false)]
    [InlineData("[General]\n bUseMyGamesDirectory = 0 \n", false)]
    [InlineData("[general]\nbusemygamesdirectory=0\n", false)]
    [InlineData("[General]\nbUseMyGamesDirectory=1\n", true)]
    [InlineData("[General]\nSLanguage=ENGLISH\n", true)]
    [InlineData("", true)]
    // The key only counts in [General], like libloadorder reads it.
    [InlineData("[Display]\nbUseMyGamesDirectory=0\n", true)]
    [InlineData("[Display]\nfoo=1\n[General]\nbUseMyGamesDirectory=0\n", false)]
    [InlineData("[General]\n; bUseMyGamesDirectory=0\n", true)]
    public void UsesMyGamesDirectoryOnlyWhenTheKeyInGeneralIsNotZero(string text, bool expected) =>
        OblivionIni.UsesMyGamesDirectory(text).Should().Be(expected);
}
