using FluentAssertions;
using NexusMods.Sdk.Games;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

public class GamePathFilterTests(ITestOutputHelper helper) : AStubbedGameTest<GamePathFilterTests>(helper)
{
    [Theory]
    [InlineData("a/b.sav")]
    [InlineData("")]
    public void SavesLocation_IsFilteredForEveryPath(string path)
    {
        Synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.Saves, path)).Should().BeTrue();
    }

    [Theory]
    [InlineData("Game", "a.dll")]
    [InlineData("Preferences", "x.ini")]
    public void OtherLocations_AreNotFiltered(string location, string path)
    {
        Synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.From(location), path)).Should().BeFalse();
    }
}
