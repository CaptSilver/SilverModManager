using FluentAssertions;
using SilverModManager.Wabbajack.Gallery;

namespace SilverModManager.Wabbajack.Tests.Gallery;

public class GalleryVisibilityTests
{
    private static readonly IReadOnlyList<GalleryEntry> Entries = GalleryReader.ReadModlists(GalleryFixtureFiles.Read("modlists-official.json"), "wabbajack-tools/mod-lists");

    [Fact]
    public void AdultListsAreHiddenUnlessTheSettingIsOn()
    {
        Entries.Visible(showAdult: false).Select(e => e.MachineUrl).Should().NotContain("spicy-fallout");
        Entries.Visible(showAdult: true).Select(e => e.MachineUrl).Should().Contain("spicy-fallout");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ListsThatAreForcedDownAreNeverShown(bool showAdult)
    {
        Entries.Visible(showAdult).Select(e => e.MachineUrl).Should().NotContain("taken-down");
    }

    [Fact]
    public void ForAGameKeepsOnlyThatGamesLists()
    {
        Entries.Visible(showAdult: true).ForGame(SilverModManager.Sdk.Games.GameId.From("CreationEngine.SkyrimSE"))
            .Select(e => e.MachineUrl).Should().Equal("example-skyrim");
    }
}
