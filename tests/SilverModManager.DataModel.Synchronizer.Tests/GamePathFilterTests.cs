using FluentAssertions;
using SilverModManager.Sdk.Games;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

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

    [Theory]
    [InlineData("Game", "vkd3d-proton.cache")]
    [InlineData("Game", "vkd3d-proton.cache.write.0")]
    [InlineData("Game", "bin/x64/Cyberpunk2077.dxvk-cache")]
    [InlineData("AppData", "vkd3d-proton.cache")]
    public void ProtonShaderCaches_AreFiltered(string location, string path)
    {
        Synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.From(location), path)).Should().BeTrue();
    }

    [Theory]
    [InlineData("Game", "cache.txt")]
    [InlineData("Game", "bin/vkd3d-proton.cache.txt/readme.md")]
    public void SimilarlyNamedFiles_AreNotFiltered(string location, string path)
    {
        Synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.From(location), path)).Should().BeFalse();
    }
}
