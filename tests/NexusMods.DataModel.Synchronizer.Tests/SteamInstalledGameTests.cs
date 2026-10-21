using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Steam;
using NexusMods.Abstractions.Steam.Values;
using NexusMods.Networking.Steam;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Backend;
using NexusMods.CrossPlatform;
using NexusMods.Games.Generic;
using NexusMods.Games.RedEngine;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.Games.TestFramework.Helpers;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// Cyberpunk 2077 installed from a Steam library on a symlinked mount, taken through the real service graph:
/// the game is found by the Steam locator, its baseline comes from depotcache manifests written the way
/// SteamKit writes them, and nothing between the game folder and the diagnostics is stubbed.
/// </summary>
public class SteamInstalledGameTests(ITestOutputHelper helper) : ASteamInstalledGameTest<SteamInstalledGameTests, Cyberpunk2077Game>(helper)
{
    private static readonly string[] PlayerFiles = ["archive/pc/mod/appearance.archive", "r6/scripts/tweak.reds", "bin/x64/plugins/cyber_engine_tweaks.log"];

    private static readonly string[] AppliedModFiles = ["archive/pc/mod/first.archive", "r6/scripts/second.reds"];

    /// <summary>Holds up the baseline build, which reads the manifests first, for as long as a test keeps it closed.</summary>
    private readonly ManualResetEventSlim _manifestGate = new(initialState: true);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        // Registered ahead of the base graph, whose own registration only fills a gap.
        services.AddSingleton<IDepotManifestSource>(new GatedManifestSource(new LocalDepotManifests(NullLogger<LocalDepotManifests>.Instance), _manifestGate));
        return base.AddServices(services)
            .AddOSInterop()
            .AddRuntimeDependencies()
            .AddGenericGameSupport()
            .AddFakeToolRunner()
            .AddRedEngineGames();
    }

    protected override SteamInstall InstallGame(SteamLibraryFixture library)
    {
        var install = CyberpunkSteamInstall.Install(library);
        CyberpunkSteamInstall.AddPlayerFiles(install);
        return install;
    }

    private IFileHashesService Hashes => ServiceProvider.GetRequiredService<IFileHashesService>();

    private IDeploymentPlanStore Plans => ServiceProvider.GetRequiredService<IDeploymentPlanStore>();

    private static readonly GamePath GameRoot = new(LocationId.Game, RelativePath.Empty);

    private IEnumerable<string> FullyHashed => FullHashes.Paths.Select(p => Path.GetRelativePath(GameFolder.ToString(), p.ToString()));

    private async Task AddAndSaveMod(Loadout.ReadOnly loadout)
    {
        using var tx = Connection.BeginTransaction();
        await AddModAsync(tx, AppliedModFiles.Select(p => (RelativePath)p), loadout.LoadoutId, "Applied mod");
        await tx.Commit();
    }

    private sealed class GatedManifestSource(IDepotManifestSource inner, ManualResetEventSlim gate) : IDepotManifestSource
    {
        public bool TryGetManifestPath(AbsolutePath steamRoot, ManifestId manifestId, AbsolutePath? installPath, out AbsolutePath manifestPath)
            => inner.TryGetManifestPath(steamRoot, manifestId, installPath, out manifestPath);

        public LocalManifestResult Read(AbsolutePath manifestPath)
        {
            gate.Wait();
            return inner.Read(manifestPath);
        }
    }

    private static string[] Ids(IEnumerable<Diagnostic> diagnostics) => diagnostics.Select(d => d.Id.ToString()).ToArray();

    [Fact]
    public async Task ManagingTheGameBuildsABaselineFromTheDepotManifests()
    {
        GameFolder.ToString().Should().StartWith(Library.Library.ToString(), "the game is found through the library list, behind the symlink");

        await CreateLoadout();

        var baseline = LocalBaseline.All(Hashes.Local).Should().ContainSingle().Subject;
        baseline.Kind.Should().Be(LocalBaselineKind.DepotManifests);
        baseline.Files.Select(f => f.Path.ToString()).Should().BeEquivalentTo(
            CyberpunkSteamInstall.GameFiles.Concat(CyberpunkSteamInstall.ExpansionFiles).Select(f => f.Path));
        NonVanillaFile.FindByBaseline(Hashes.Local, baseline.Id).Select(f => f.Path.ToString()).Should().BeEquivalentTo(PlayerFiles);
    }

    [Fact]
    public async Task ManagingTheGameReadsOnlyTheFilesTheBaselineDoesNotVouchFor()
    {
        await CreateLoadout();

        FullyHashed.Should().BeEquivalentTo(PlayerFiles, "every vanilla file is hashed once, by the baseline, and not again by the index");
    }

    [Fact]
    public async Task IndexingWhileTheBaselineIsStillBeingBuiltWaitsForItAndDoesNotReadTheVanillaFilesAgain()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        _manifestGate.Reset();

        var build = Hashes.EnsureLocalBaseline(GameInstallation);
        var index = Synchronizer.ReindexState(GameInstallation);
        await Task.Delay(TimeSpan.FromSeconds(1));
        index.IsCompleted.Should().BeFalse("the index has to wait for the rows the build is about to commit");

        _manifestGate.Set();
        await Task.WhenAll(build, index);

        FullyHashed.Should().BeEquivalentTo(PlayerFiles);
    }

    [Fact]
    public async Task HealthCheckOfAFreshlyManagedGameNamesTheNonVanillaFilesAndReportsNothingMissing()
    {
        var loadout = await CreateLoadout();

        var diagnostics = await RunEveryEmitter(loadout);

        // The prefix Proton wrote is bare, so the Wine setup checks have their say; the file checks must not.
        Ids(diagnostics).Should().Equal(
            "NexusMods.Games.FileHashes: 3",
            "NexusMods.Games.RedEngine.Cyberpunk2077: 1",
            "NexusMods.Games.RedEngine.Cyberpunk2077: 5",
            "NexusMods.Games.RedEngine.Cyberpunk2077: 6");
        var differ = Describe(diagnostics[0]);
        differ.Should().Contain("3 file(s) in the Cyberpunk 2077 folder");
        foreach (var file in PlayerFiles) differ.Should().Contain(file);
    }

    [Fact]
    public async Task HealthCheckStaysCleanOfMissingFilesAfterAnApplyOnCopy()
    {
        var loadout = await CreateLoadout();
        await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, DeploymentBackendId.Copy, chosen: true);
        await AddAndSaveMod(loadout);

        await Synchronizer.Synchronize(Refresh(loadout));
        var diagnostics = await RunEveryEmitter(Refresh(loadout));

        foreach (var file in AppliedModFiles) (GameFolder / file).FileExists.Should().BeTrue();
        Ids(diagnostics).Should().NotContain(["NexusMods.Games.FileHashes: 2", "NexusMods.Games.FileHashes: 4"]);
    }

    [Fact]
    public async Task ReindexingAnAppliedGameOnCopyReadsNoFileInFull()
    {
        var loadout = await CreateLoadout();
        await SynchronizerService.ChangeDeploymentBackend(GameInstallation, GameRoot, DeploymentBackendId.Copy, chosen: true);
        await AddAndSaveMod(loadout);
        await Synchronizer.Synchronize(Refresh(loadout));
        FullHashes.Reset();

        await Synchronizer.ReindexState(GameInstallation);
        await Synchronizer.Synchronize(Refresh(loadout));

        FullyHashed.Should().BeEmpty("nothing changed since the last apply");
    }

    [Fact]
    public async Task ReindexingAnAppliedGameOnTheLinkChainReadsNoFileInFull()
    {
        var loadout = await CreateLoadout();
        await AddAndSaveMod(loadout);
        await Synchronizer.Synchronize(Refresh(loadout));
        var metadataId = GameRegistry.ForceGetMetadata(GameInstallation).Id;
        Plans.Load(Connection.Db, metadataId).BackendFor(GameRoot).Should().NotBe(DeploymentBackendId.Copy, "the chain picks a link backend on a folder it can link into");
        FullHashes.Reset();

        await Synchronizer.ReindexState(GameInstallation);
        await Synchronizer.Synchronize(Refresh(loadout));

        FullyHashed.Should().BeEmpty("nothing changed since the last apply");
        foreach (var file in AppliedModFiles) (GameFolder / file).FileExists.Should().BeTrue();
    }

    [Fact]
    public async Task CleaningACoveredGameFolderRemovesThePlayersFiles()
    {
        var loadout = await CreateLoadout();

        await LoadoutManager.CleanGameFolder(Refresh(loadout));

        foreach (var file in PlayerFiles) (GameFolder / file).FileExists.Should().BeFalse();
        foreach (var file in CyberpunkSteamInstall.GameFiles) (GameFolder / file.Path).FileExists.Should().BeTrue("the game's own files stay");
    }
}
