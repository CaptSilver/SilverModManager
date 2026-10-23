using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Backend;
using SilverModManager.CrossPlatform;
using SilverModManager.Games.Generic;
using SilverModManager.Games.RedEngine;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// Cyberpunk 2077 with the Phantom Liberty depot installed but its manifest missing from every depotcache, and
/// a central hashes database that has no rows for the build either. The build is not covered, so the vanilla
/// file list is incomplete and anything that deletes by it has to refuse.
/// </summary>
public class SteamInstalledWithoutAManifestTests(ITestOutputHelper helper) : ASteamInstalledGameTest<SteamInstalledWithoutAManifestTests, Cyberpunk2077Game>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddOSInterop()
            .AddRuntimeDependencies()
            .RemoveRuntimeDependencyChecker()
            .AddGenericGameSupport()
            .AddFakeToolRunner()
            .AddRedEngineGames();
    }

    protected override SteamInstall InstallGame(SteamLibraryFixture library)
    {
        var uncached = CyberpunkSteamInstall.ExpansionDepot with { Cache = ManifestCache.None };
        var install = CyberpunkSteamInstall.Install(library, CyberpunkSteamInstall.GameDepot, uncached);
        CyberpunkSteamInstall.AddPlayerFiles(install);
        return install;
    }

    private IFileHashesService Hashes => ServiceProvider.GetRequiredService<IFileHashesService>();

    private Dictionary<string, string> FolderContents() =>
        Directory.GetFiles(GameFolder.ToString(), "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllText);

    private static int ExternalChanges(Loadout.ReadOnly loadout) =>
        LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Count(f => f.AsLoadoutItem().Parent.AsLoadoutItem().Name == "Overrides");

    [Fact]
    public async Task TheBaselineIsPartialAndTheBuildIsNotCovered()
    {
        await CreateLoadout();

        LocalBaseline.All(Hashes.Local).Should().ContainSingle().Which.Kind.Should().Be(LocalBaselineKind.PartialDepotManifests);
        Hashes.CoversInstalledBuild((GameInstallation.LocatorResult.Store, GameInstallation.LocatorResult.LocatorIds.ToArray()))
            .Should().BeFalse("one depot of the build has no rows in any store");
    }

    [Fact]
    public async Task CleanGameFolderRefusesAndLeavesTheFolderAndItsRowsAlone()
    {
        var loadout = await CreateLoadout();
        var before = FolderContents();
        var rows = ExternalChanges(loadout);

        var act = async () => await LoadoutManager.CleanGameFolder(Refresh(loadout));

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
        FolderContents().Should().BeEquivalentTo(before);
        ExternalChanges(Refresh(loadout)).Should().Be(rows);
    }

    [Fact]
    public async Task UnManagingRefusesAndKeepsTheLoadout()
    {
        await CreateLoadout();
        var before = FolderContents();

        var act = async () => await LoadoutManager.UnManage(GameInstallation);

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
        Loadout.All(Connection.Db).Should().ContainSingle();
        FolderContents().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task ResettingToTheOriginalGameStateRefuses()
    {
        await CreateLoadout();
        var before = FolderContents();

        var act = async () => await Synchronizer.ResetToOriginalGameState(GameInstallation, GameInstallation.LocatorResult.LocatorIds.ToArray());

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
        FolderContents().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task HealthCheckStillRunsEveryEmitterWithoutThrowing()
    {
        var loadout = await CreateLoadout();

        var diagnostics = await RunEveryEmitter(loadout);

        diagnostics.Select(d => d.Id.ToString()).Should().NotContain(["SilverModManager.Games.FileHashes: 2", "SilverModManager.Games.FileHashes: 4"]);
    }

    [Fact]
    public async Task ADepotManifestThatAppearsLaterCompletesTheBaselineAndLiftsTheRefusal()
    {
        var loadout = await CreateLoadout();
        Library.RestoreManifest(CyberpunkSteamInstall.ExpansionDepot);
        Relocate();
        await Hashes.EnsureLocalBaseline(GameInstallation);

        await LoadoutManager.CleanGameFolder(Refresh(loadout));

        LocalBaseline.All(Hashes.Local).Should().ContainSingle().Which.Kind.Should().Be(LocalBaselineKind.DepotManifests);
        ExternalChanges(Refresh(loadout)).Should().Be(0);
    }
}
