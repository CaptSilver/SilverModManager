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
        => LocationId.From("DisplayNameTestsNoName").DisplayName.Should().Be("Game data");
}
