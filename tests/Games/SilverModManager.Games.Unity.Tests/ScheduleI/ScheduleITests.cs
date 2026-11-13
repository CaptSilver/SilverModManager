using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Unity.MelonLoader;
using SilverModManager.Games.Unity.Tests.Fixtures;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Unity.Tests;

public class ScheduleITests(ITestOutputHelper outputHelper) : AMelonLoaderRowTests<ScheduleITests>(outputHelper, UnityGames.ScheduleI)
{
    private AUnityModLoader Loader => ((IUnityGame)Game).Loader;

    [Fact]
    public void TheGameIsIdentifiedByItsSteamAppIdAndItsNexusModsId()
    {
        ((IGameData)Game).GameId.Should().Be(GameId.From("Unity.ScheduleI"));
        ((IGameData)Game).DisplayName.Should().Be("Schedule I");
        Game.StoreIdentifiers.SteamAppIds.Should().Equal(3164500u);
        ((IGameData)Game).NexusModsGameId.Should().Be(Optional<Sdk.NexusModsApi.NexusModsGameId>.Create(Sdk.NexusModsApi.NexusModsGameId.From(7381)));
    }

    [Fact]
    public void TheGameTakesItsModsFromThunderstore()
    {
        var thunderstore = ThunderstoreGame.For(Game);

        thunderstore.Should().BeSameAs(Game);
        thunderstore!.ThunderstoreCommunity.Should().Be("schedule-i");
        thunderstore.ThunderstoreLoaderPackage.Should().Be("LavaGang-MelonLoader");
        thunderstore.ThunderstoreLoaderAliases.Should().BeEmpty();
    }

    [Fact]
    public void TheGameFolderIsTheOnlyLocation()
    {
        GameInstallation.Locations.Keys.Should().BeEquivalentTo([LocationId.Game]);
    }

    [Fact]
    public void TheLoaderIsMelonLoader()
    {
        Loader.Should().BeOfType<MelonLoaderLoader>();
        Loader.Proxy.Should().Be(new GamePath(LocationId.Game, "version.dll"));
    }

    [Fact]
    public void TheGameInstallsThePackBeforeMods()
    {
        Game.LibraryItemInstallers.Select(installer => installer.GetType())
            .Should().Equal(typeof(MelonLoaderPackInstaller), typeof(MelonLoaderModInstaller));
    }

    [Fact]
    public void TheGameReportsAMissingLoaderAWrongBranchAndPrefixComponents()
    {
        Game.DiagnosticEmitters.OfType<UnityLoaderMissingEmitter>().Should().ContainSingle();
        Game.DiagnosticEmitters.OfType<UnityRuntimeBranchEmitter>().Should().ContainSingle();
        Game.DiagnosticEmitters.OfType<WineDllOverrideEmitter>().Should().ContainSingle();
        Game.DiagnosticEmitters.OfType<WinePrefixComponentsEmitter>().Should().ContainSingle();
        Game.Should().BeAssignableTo<IWinePrefixRequirements>();
    }

    [Fact]
    public async Task TheWineOverrideIsVersionOnceThePackIsInstalled()
    {
        var loadout = await CreateLoadout();
        Loader.RequiredWineDllOverrides(loadout).Should().BeEmpty();

        await Install(PackInstaller, loadout, await AddArchiveNamed("LavaGang-MelonLoader-0.7.3.zip", PackPaths));

        var overrides = WineDllOverrideRequirements.For(GameInstallation.Game, Refresh(loadout));
        overrides.Should().ContainSingle().Which.DllName.Should().Be("version");
        overrides.Single().OverrideTypes.Should().Equal(WineDllOverrideType.Native, WineDllOverrideType.BuiltIn);
    }

    [Fact]
    public async Task AThunderstoreMelonLoaderPackAndModInstallToTheRootAndMods()
    {
        var (pack, packTargets) = await TryInstall(PackInstaller, await AddArchiveNamed("LavaGang-MelonLoader-0.7.3.zip", "version.dll", "MelonLoader/net6/MelonLoader.dll", "manifest.json"));
        var (mod, modTargets) = await TryInstall(ModInstaller, await AddArchiveNamed("Someone-SomeMod-1.0.0.zip", "SomeMod.dll"));

        pack.IsSuccess.Should().BeTrue();
        packTargets.Should().BeEquivalentTo(OnGame("version.dll"), OnGame("MelonLoader/net6/MelonLoader.dll"));
        mod.IsSuccess.Should().BeTrue();
        modTargets.Should().BeEquivalentTo(OnGame("Mods/SomeMod.dll"));
    }
}

public class ScheduleILoaderMissingEmitterTests(ITestOutputHelper outputHelper)
    : ALoadoutDiagnosticEmitterTest<ScheduleILoaderMissingEmitterTests, IGame, UnityLoaderMissingEmitter>(outputHelper, UnityGames.ScheduleI.GameId)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddUnityGames()
            .AddUniversalGameLocator(UnityGames.ScheduleI.GameId, locationsFileSystem: IsolatedKnownFolders.Create);
    }

    [Fact]
    public async Task TheMissingLoaderReportNamesMelonLoader()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout, "Someone-Foo");
            AddFile(tx, loadout, group, new GamePath(LocationId.Game, "Mods/Foo.dll"));
            await tx.Commit();
        }

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.Unity", number: 1));
        var details = diagnostic.FormatDetails(ServiceProvider.GetRequiredService<IDiagnosticWriter>());
        details.Should().Contain("MelonLoader").And.Contain("`LavaGang-MelonLoader`");
    }
}
