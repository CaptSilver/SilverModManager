using System.Collections.Frozen;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.FileHashes.Emitters;
using SilverModManager.Games.RedEngine;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using NexusMods.Paths;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A game folder whose Steam install has no cached depot manifests, so the vanilla baseline is a snapshot of
/// whatever was on disk when the app first looked, old mod files included. Those files are Game-layer rows that
/// nothing but the disk vouches for.
/// </summary>
public class SnapshotBaselineDeletionsTests(ITestOutputHelper helper) : ASteamInstalledGameTest<SnapshotBaselineDeletionsTests, Cyberpunk2077Game>(helper)
{
    private const string Extra = "r6/scripts/tweak.reds";
    private const string Vanilla = "bin/x64/Cyberpunk2077.exe";
    private static readonly GamePath ExtraPath = new(LocationId.Game, Extra);

    private readonly CapturedLogs _logs = new();

    private IFileHashesService Hashes => ServiceProvider.GetRequiredService<IFileHashesService>();

    protected override IServiceCollection AddServices(IServiceCollection services)
        => _logs.AddTo(base.AddServices(services).AddRedEngineGames());

    protected override SteamInstall InstallGame(SteamLibraryFixture library)
    {
        var install = CyberpunkSteamInstall.Install(library);
        CyberpunkSteamInstall.AddPlayerFiles(install);
        foreach (var depot in new[] { CyberpunkSteamInstall.GameDepot, CyberpunkSteamInstall.ExpansionDepot })
            library.RemoveManifest(depot.DepotId, depot.ManifestId);
        return install;
    }

    private async Task<Dictionary<GamePath, SyncNode>> Tree(Loadout.ReadOnly loadout)
    {
        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        return tree;
    }

    private async Task<Diagnostic[]> WhatTheMissingGameFilesEmitterSays(Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> tree)
    {
        var emitter = new UndeployableLoadoutDueToMissingGameFiles(ServiceProvider);
        var found = new List<Diagnostic>();
        await foreach (var diagnostic in emitter.Diagnose(Refresh(loadout), tree.ToFrozenDictionary(), CancellationToken.None))
            found.Add(diagnostic);
        return found.ToArray();
    }

    private void DeleteFromDisk(string path) => (GameFolder / path).Delete();

    [Fact]
    public async Task SnapshotGameFileDeletedOnDisk_IsRecordedAsADeleteNotAWarning()
    {
        var loadout = await CreateLoadout();
        (await Tree(loadout))[ExtraPath].SourceItemType.Should().Be(LoadoutSourceItemType.Game, "the snapshot recorded the file as vanilla");

        DeleteFromDisk(Extra);
        var tree = await Tree(loadout);

        var node = tree[ExtraPath];
        node.PlayerDeleted.Should().BeTrue();
        node.Actions.Should().Be(Actions.AddReifiedDelete);
        node.Actions.Should().NotHaveFlag(Actions.WarnOfUnableToExtract);
        (await WhatTheMissingGameFilesEmitterSays(loadout, tree)).Should().BeEmpty();
    }

    [Fact]
    public async Task SnapshotGameFileDeletedOnDisk_TheNextSyncIsQuiet()
    {
        var loadout = await CreateLoadout();
        DeleteFromDisk(Extra);

        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        (GameFolder / Extra).FileExists.Should().BeFalse("a deleted file is not recreated");
        var again = await Tree(loadout);
        _logs.Lines.Where(l => l.Level == LogLevel.Information && l.Message.Contains(Extra)).Should().ContainSingle("the deletion is logged once, on the apply that records it");
        again.Values.Should().OnlyContain(n => n.Actions == Actions.DoNothing);
        (await Synchronizer.ShouldSynchronize(Refresh(loadout), Synchronizer.GetLastScannedDiskState(GameRegistry.ForceGetMetadata(GameInstallation)), Synchronizer.GetDiskStateForGame(GameRegistry.ForceGetMetadata(GameInstallation))))
            .Should().BeFalse("nothing is left to apply");
    }

    // The player deleted the file after the snapshot recorded it and before any apply, so no previous state ever held
    // it. Creating the loadout is the first sync, and it is the one that has to record the deletion.
    private async Task<Loadout.ReadOnly> LoadoutAfterDeletingBeforeTheAppRecordedIt(string path)
    {
        (await Hashes.EnsureLocalBaseline(GameInstallation)).Kind.Should().Be(LocalBaselineKind.Snapshot);
        DeleteFromDisk(path);
        return await CreateLoadout();
    }

    [Fact]
    public async Task SnapshotGameFileDeletedBeforeTheAppRecordedIt_IsRecordedAsADeleteNotAPermanentWarning()
    {
        var loadout = await LoadoutAfterDeletingBeforeTheAppRecordedIt(Extra);

        ((ALoadoutSynchronizer)Synchronizer).WinningFilesForTest(Refresh(loadout))
            .Single(f => f.Path == ExtraPath).ItemType.Should().Be("Deleted", "the first sync recorded the player's deletion");
        var tree = await Tree(loadout);
        tree.Should().NotContainKey(ExtraPath);
        tree.Values.Should().NotContain(n => n.Actions.HasFlag(Actions.WarnOfUnableToExtract));
        (await WhatTheMissingGameFilesEmitterSays(loadout, tree)).Should().BeEmpty();
    }

    [Fact]
    public async Task SnapshotGameFileDeletedBeforeTheAppRecordedIt_LeavesNothingPendingAfterTheNextApply()
    {
        var loadout = await LoadoutAfterDeletingBeforeTheAppRecordedIt(Extra);

        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        (GameFolder / Extra).FileExists.Should().BeFalse("a deleted file is not recreated");
        (await Tree(loadout)).Values.Should().OnlyContain(n => n.Actions == Actions.DoNothing);
        (await Synchronizer.ShouldSynchronize(Refresh(loadout), Synchronizer.GetLastScannedDiskState(GameRegistry.ForceGetMetadata(GameInstallation)), Synchronizer.GetDiskStateForGame(GameRegistry.ForceGetMetadata(GameInstallation))))
            .Should().BeFalse("nothing is left to apply");
    }

    [Fact]
    public async Task SnapshotGameFilePresentOnDisk_IsLeftAlone()
    {
        var loadout = await LoadoutAfterDeletingBeforeTheAppRecordedIt(Extra);

        var node = (await Tree(loadout))[new GamePath(LocationId.Game, Vanilla)];

        node.HaveDisk.Should().BeTrue();
        node.PlayerDeleted.Should().BeFalse();
        node.Actions.Should().Be(Actions.DoNothing);
    }

    // A snapshot taken before a mod-shape rule existed holds a hand-installed mod as the game's own file. The loadout was
    // applied with that row, so the file is on disk and in the previous state. When the baseline is re-checked and the row
    // retracted, the file must become the player's (an Overrides row), not a mod the loadout no longer has and so removes.
    [Fact]
    public async Task SnapshotRowRetractedAfterAnApply_KeepsTheFileAsAnOverridesRowInsteadOfDeletingIt()
    {
        const string modFile = "src/x.dll";
        var loadout = await CreateLoadout();
        (GameFolder / modFile).Parent.CreateDirectory();
        await (GameFolder / modFile).WriteAllTextAsync("hand-installed upscaler");
        await LocalBaselineRows.AddFileToBaseline(Hashes.Local.Connection, LocalBaseline.All(Hashes.Local).Single().Id, modFile, "hand-installed upscaler");
        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        var path = new GamePath(LocationId.Game, modFile);
        (await Tree(loadout))[path].SourceItemType.Should().Be(LoadoutSourceItemType.Game, "the stale snapshot row still counts it as vanilla");

        await Hashes.EnsureLocalBaseline(GameInstallation);

        var node = (await Tree(loadout))[path];
        node.Actions.Should().NotHaveFlag(Actions.DeleteFromDisk, "the retracted file is not a mod the loadout dropped");
        node.Actions.Should().HaveFlag(Actions.IngestFromDisk);
        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        (GameFolder / modFile).FileExists.Should().BeTrue();
        ((ALoadoutSynchronizer)Synchronizer).WinningFilesForTest(Refresh(loadout))
            .Single(f => f.Path == path).ItemType.Should().Be("Loadout");
        (await Tree(loadout)).Values.Should().NotContain(n => n.Actions.HasFlag(Actions.DeleteFromDisk));
    }

    // With no hash store knowing the build, a real vanilla file is as unvouched-for as a snapshot mod file, so the
    // same rule applies: the delete is recorded and the missing-game-files card stays quiet.
    [Fact]
    public async Task SnapshotVanillaFileDeletedOnDisk_IsRecordedAsADeleteToo()
    {
        var loadout = await CreateLoadout();
        DeleteFromDisk(Vanilla);

        var tree = await Tree(loadout);

        var node = tree[new GamePath(LocationId.Game, Vanilla)];
        node.PlayerDeleted.Should().BeTrue();
        node.Actions.Should().Be(Actions.AddReifiedDelete);
        (await WhatTheMissingGameFilesEmitterSays(loadout, tree)).Should().BeEmpty();
    }

    // The rule only touches a node already at WarnOfUnableToExtract. With the bytes in the store the table gives
    // xAA_xXX_iG => AddReifiedDelete (already a recorded delete) or xAB_xxX_iG => ExtractToDisk, never the warning.
    [Fact]
    public async Task SnapshotGameFileWithStoredBytes_KeepsTheTablesAction()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)Extra], loadout.LoadoutId, "Mod over the file");
            await tx.Commit();
        }
        await Synchronizer.Synchronize(Refresh(loadout));
        using (var tx = Connection.BeginTransaction())
        {
            foreach (var mod in LoadoutItem.FindByLoadout(Connection.Db, loadout).Where(item => item.Name == "Mod over the file"))
                tx.Delete(mod.Id, recursive: true);
            await tx.Commit();
        }
        await Synchronizer.Synchronize(Refresh(loadout));

        DeleteFromDisk(Extra);
        var node = (await Tree(loadout))[ExtraPath];

        node.SourceItemType.Should().Be(LoadoutSourceItemType.Game);
        node.HaveDisk.Should().BeFalse();
        node.HavePrevious.Should().BeTrue();
        node.Signature.Should().HaveFlag(Signature.LoadoutArchived);
        node.Actions.Should().Be(ActionMapping.MapActions(node.Signature));
        node.Actions.Should().NotHaveFlag(Actions.WarnOfUnableToExtract);
    }
}
