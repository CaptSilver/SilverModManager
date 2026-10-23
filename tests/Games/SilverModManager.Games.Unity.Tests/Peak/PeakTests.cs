using DynamicData.Kernel;
using FluentAssertions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.Unity.BepInEx;
using SilverModManager.Games.Unity.Tests.Fixtures;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity.Tests;

public class PeakTests(ITestOutputHelper outputHelper) : AUnityGameTests<PeakTests, Unity.Peak.Peak>(outputHelper)
{
    [Fact]
    public void TheGameIsIdentifiedByItsSteamAppIdAndHasNoNexusId()
    {
        ((IGameData)Game).GameId.Should().Be(GameId.From("Unity.Peak"));
        ((IGameData)Game).DisplayName.Should().Be("PEAK");
        Game.StoreIdentifiers.SteamAppIds.Should().Equal(3527290u);
        ((IGameData)Game).NexusModsGameId.Should().Be(Optional<Sdk.NexusModsApi.NexusModsGameId>.None);
    }

    [Fact]
    public void ThePrimaryFileIsTheExecutableInTheGameFolder()
    {
        Game.GetPrimaryFile(GameInstallation).Should().Be(new GamePath(LocationId.Game, "PEAK.exe"));
    }

    [Fact]
    public void TheGameFolderIsTheOnlyLocation()
    {
        GameInstallation.Locations.Keys.Should().BeEquivalentTo([LocationId.Game]);
    }

    [Fact]
    public void TheGameTakesItsModsFromThunderstore()
    {
        var thunderstore = ThunderstoreGame.For(Game);

        thunderstore.Should().BeSameAs(Game);
        thunderstore!.ThunderstoreCommunity.Should().Be("peak");
        thunderstore.ThunderstoreLoaderPackage.Should().Be("BepInEx-BepInExPack_PEAK");
        thunderstore.ThunderstoreLoaderAliases.Should().Equal("BepInEx-BepInExPack", "bbepis-BepInExPack", "xiaoxiao921-BepInExPack");
    }

    [Fact]
    public void TheGameDeclaresWineDllOverrides()
    {
        Game.Should().BeAssignableTo<IWineDllOverrideRequirements>();
    }

    [Fact]
    public void TheGameInstallsTheLoaderPack()
    {
        Game.LibraryItemInstallers.OfType<BepInExPackInstaller>().Should().ContainSingle();
    }

    [Fact]
    public void TheGameInstallsThePackBeforePlugins()
    {
        Game.LibraryItemInstallers.Select(installer => installer.GetType())
            .Should().Equal(typeof(BepInExPackInstaller), typeof(BepInExPluginInstaller));
    }

    [Fact]
    public void TheGameReportsAMissingLoaderAndAMissingWineOverride()
    {
        Game.DiagnosticEmitters.OfType<BepInExMissingEmitter>().Should().ContainSingle();
        Game.DiagnosticEmitters.OfType<WineDllOverrideEmitter>().Should().ContainSingle();
    }
}
