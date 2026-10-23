using DynamicData.Kernel;
using FluentAssertions;
using NexusMods.Abstractions.Games;
using NexusMods.Games.Generic.Diagnostics;
using NexusMods.Games.Unity.BepInEx;
using NexusMods.Games.Unity.Tests.Fixtures;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.Unity.Tests;

public class RepoTests(ITestOutputHelper outputHelper) : AUnityGameTests<RepoTests, Unity.Repo.Repo>(outputHelper)
{
    [Fact]
    public void TheGameIsIdentifiedByItsSteamAppIdAndHasNoNexusId()
    {
        ((IGameData)Game).GameId.Should().Be(GameId.From("Unity.Repo"));
        ((IGameData)Game).DisplayName.Should().Be("R.E.P.O.");
        Game.StoreIdentifiers.SteamAppIds.Should().Equal(3241660u);
        ((IGameData)Game).NexusModsGameId.Should().Be(Optional<Sdk.NexusModsApi.NexusModsGameId>.None);
    }

    [Fact]
    public void ThePrimaryFileIsTheExecutableInTheGameFolder()
    {
        Game.GetPrimaryFile(GameInstallation).Should().Be(new GamePath(LocationId.Game, "REPO.exe"));
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
        thunderstore!.ThunderstoreCommunity.Should().Be("repo");
        thunderstore.ThunderstoreLoaderPackage.Should().Be("BepInEx-BepInExPack");
        thunderstore.ThunderstoreLoaderAliases.Should().Equal("bbepis-BepInExPack", "xiaoxiao921-BepInExPack");
    }

    [Fact]
    public void TheGameDeclaresWineDllOverrides()
    {
        Game.Should().BeAssignableTo<IWineDllOverrideRequirements>();
    }

    [Theory]
    [InlineData("BepInEx/LogOutput.log", true)]
    [InlineData("BepInEx/cache/chainloader_typeloader.dat", true)]
    [InlineData("BepInEx/plugins/X/X.dll", false)]
    public void TheGamesSynchronizerIgnoresWhatBepInExWritesWhileTheGameRuns(string path, bool ignored)
    {
        var synchronizer = Game.Synchronizer.Should().BeOfType<UnityGameSynchronizer>().Subject;

        synchronizer.GamePathFilter.ShouldFilter(new GamePath(LocationId.Game, path)).Should().Be(ignored);
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
