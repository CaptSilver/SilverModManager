using FluentAssertions;
using SilverModManager.App.UI.Helpers;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.UI.Tests;

public class ThunderstoreStubbedGameTests(IServiceProvider provider)
{
    [Fact]
    public void Stub_IsNotOnNexusMods()
    {
        new ThunderstoreStubbedGame(provider).IsOnNexusMods().Should().BeFalse();
    }

    [Fact]
    public void Stub_InstallsWithTheStubbedInstaller()
    {
        new ThunderstoreStubbedGame(provider).LibraryItemInstallers.Should().ContainSingle()
            .Which.Should().BeOfType<StubbedGameInstaller>();
    }

    [Fact]
    public void Stub_IsAThunderstoreGameForTheRepoCommunity()
    {
        var thunderstore = ThunderstoreGame.For(new ThunderstoreStubbedGame(provider));

        thunderstore.Should().NotBeNull();
        thunderstore!.ThunderstoreCommunity.Should().Be("repo");
        thunderstore.ThunderstoreLoaderPackage.Should().Be("BepInEx-BepInExPack");
        thunderstore.ThunderstoreLoaderAliases.Should().Equal("bbepis-BepInExPack", "xiaoxiao921-BepInExPack");
    }
}
