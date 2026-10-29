using FluentAssertions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Sdk.Tests;

public class LocationIdDisplayNameTests
{
    [Theory]
    [InlineData("Saves", "Saves")]
    [InlineData("Preferences", "Settings")]
    [InlineData("AppData", "AppData")]
    [InlineData("AppDataRoaming", "AppData")]
    public void BuiltInLocations_UseThePlayersWord(string id, string expected)
        => LocationId.From(id).DisplayName.Should().Be(expected);

    [Fact]
    public void ARegisteredName_IsWhatThePlayerSees()
        => LocationId.From("DisplayNameTestsRegistered", "Documents").DisplayName.Should().Be("Documents");

    [Fact]
    public void LookingTheIdUpAgain_KeepsTheRegisteredName()
    {
        LocationId.From("DisplayNameTestsAgain", "Documents");

        LocationId.From("DisplayNameTestsAgain").DisplayName.Should().Be("Documents");
    }

    [Fact]
    public void AnIdWithNoName_GetsANeutralLabelNotTheId()
    {
        var id = LocationId.From("DisplayNameTestsNoName");

        id.DisplayName.Should().Be("Other folders");
        id.IsNamed.Should().BeFalse();
    }

    [Fact]
    public void Unknown_IsNotNamedSoItReadsAsOtherFolders()
    {
        LocationId.Unknown.IsNamed.Should().BeFalse();
        LocationId.Unknown.DisplayName.Should().Be(LocationId.UnnamedDisplayName);
    }

    // Each label is the prefix of a folder row ("Mods/Foo") and the end of "in <label>".
    [Theory]
    [InlineData("Me3Mods", "Mods")]
    [InlineData("Me3Host", "Mod loader folder")]
    public void Me3Locations_ReadAsAFolderPrefixAndAfterIn(string id, string expected)
        => LocationId.From(id).DisplayName.Should().Be(expected);

    [Fact]
    public void TheGameFolderLabel_IsOneSentenceFriendlyName()
        => LocationId.Game.DisplayName.Should().Be("Game folder");

    [Fact]
    public void ANamedLocation_IsNamed()
        => LocationId.From("DisplayNameTestsRegistered", "Documents").IsNamed.Should().BeTrue();

    [Fact]
    public void ASentencePhrase_IsRegisteredBesideTheLabel()
    {
        var id = LocationId.From("DisplayNameTestsPhrase", "Mod loader folder", "the mod loader folder");

        id.DisplayName.Should().Be("Mod loader folder");
        id.SentenceName.Should().Be("the mod loader folder");
    }

    [Fact]
    public void WithNoPhrase_TheSentenceReadsTheLabel()
        => LocationId.From("DisplayNameTestsNoPhrase", "Documents").SentenceName.Should().Be("Documents");

    [Fact]
    public void TheGameFolderAndTheModLoaderFolder_TakeAnArticleInASentence()
    {
        LocationId.Game.SentenceName.Should().Be("the game folder");
        LocationId.Me3Host.SentenceName.Should().Be("the mod loader folder");
    }
}
