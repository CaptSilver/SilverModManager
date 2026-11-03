using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.DataModel.Undo;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Trees;
using SilverModManager.Games.Generic;
using SilverModManager.Games.RedEngine;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.TestFramework.Helpers;
using SilverModManager.CrossPlatform;
using SilverModManager.Backend;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// What the next Apply does once the service has taken a file's rows out of the loadout: the file leaves the game
/// folder, the game's own version comes back, or, when nothing was ever on disk, the entry just leaves the preview.
/// </summary>
public class RemovedFileRowsTests(ITestOutputHelper helper) : AStubbedGameTest<RemovedFileRowsTests>(helper)
{
    private static readonly GamePath ModFile = new(LocationId.Game, "Data/mod.pak");

    private ILoadoutFileRemover Remover => ServiceProvider.GetRequiredService<ILoadoutFileRemover>();

    private AbsolutePath OnDisk(Loadout.ReadOnly loadout, GamePath path) => loadout.InstallationInstance.Locations.ToAbsolutePath(path);

    private async Task AddModFile(Loadout.ReadOnly loadout, string name, GamePath path, string content, bool storeBytes = true)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, name);
        AddFile(tx, loadout.LoadoutId, group, path, content, out var identity, out var size);
        if (storeBytes)
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.Path, new MemoryStream(Encoding.UTF8.GetBytes(content))), identity, size)]);
        await tx.Commit();
    }

    private FileChangeType? ChangeOf(Loadout.ReadOnly loadout, GamePath path) =>
        SynchronizerService.GetApplyDiffTree(loadout.LoadoutId).GetAllDescendentFiles()
            .SingleOrDefault(f => f.GamePath() == path)?.Item.Value?.ChangeType;

    private static FileRemovalRequest Request(Loadout.ReadOnly loadout, GamePath path) => new(loadout.LoadoutId, [path]);

    [Fact]
    public async Task ADeployedFileWhoseRowsWereRemovedIsDeletedByTheNextApply()
    {
        var loadout = await CreateLoadout();
        await AddModFile(loadout, "Mod", ModFile, "mod bytes");
        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        OnDisk(loadout, ModFile).FileExists.Should().BeTrue();

        await Remover.Remove(Request(loadout, ModFile), includeLocked: true);

        ChangeOf(loadout, ModFile).Should().Be(FileChangeType.Removed);
        await Synchronizer.Synchronize(Refresh(loadout));
        OnDisk(loadout, ModFile).FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task ADeployedFileEditedOnDiskAndThenRemovedIsBackedUpAndDeleted()
    {
        var loadout = await CreateLoadout();
        await AddModFile(loadout, "Mod", ModFile, "mod bytes");
        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        await OnDisk(loadout, ModFile).WriteAllTextAsync("edited by hand");

        await Remover.Remove(Request(loadout, ModFile), includeLocked: true);
        await Synchronizer.Synchronize(Refresh(loadout));

        OnDisk(loadout, ModFile).FileExists.Should().BeFalse();
        (await FileStore.HaveFile(FileIdentity.XxHash3("edited by hand".xxHash3AsUtf8()))).Should().BeTrue("the edit is archived before the file goes");
    }

    [Fact]
    public async Task AnExternalChangeWhoseRowWasRemovedIsDeletedByTheNextApply()
    {
        var loadout = await CreateLoadout();
        var placed = new GamePath(LocationId.Game, "placed.ini");
        OnDisk(loadout, placed).Parent.CreateDirectory();
        await OnDisk(loadout, placed).WriteAllTextAsync("mine");
        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        var row = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemWithTargetPath().Single(r => r.TargetPath == placed);
        row.AsLoadoutItem().Parent.AsLoadoutItem().Name.Should().Be("Overrides");

        var result = await Remover.Remove(Request(loadout, placed), includeLocked: true);
        await Synchronizer.Synchronize(Refresh(loadout));

        OnDisk(loadout, placed).FileExists.Should().BeFalse();

        var undo = ServiceProvider.GetRequiredService<UndoService>();
        var revision = undo.RevisionsFor(loadout.Id).Single(r => r.Revision.TxEntity.Value == result.RestorePoint.Value).Revision;
        await undo.RevertTo(revision);
        LoadoutItemWithTargetPath.Load(Connection.Db, row.Id).IsValid().Should().BeTrue("the revert brings the row back under its id");
    }

    [Fact]
    public async Task AFileThatWasNeverExtractedLeavesTheDiffWhenItsRowsAreRemoved()
    {
        var loadout = await CreateLoadout();
        await AddModFile(loadout, "Hollow", ModFile, "no bytes in the store", storeBytes: false);
        ChangeOf(loadout, ModFile).Should().Be(FileChangeType.Added);

        await Remover.Remove(Request(loadout, ModFile), includeLocked: true);

        ChangeOf(loadout, ModFile).Should().BeNull();
    }

    [Fact]
    public async Task RemovingAnExternalChangesDeleteMarkerStopsTheDeleteWinning()
    {
        var loadout = await CreateLoadout();
        LoadoutOverridesGroupId overrides;
        using (var tx = Connection.BeginTransaction())
        {
            overrides = ((ALoadoutSynchronizer)Synchronizer).GetOrCreateOverridesGroup(tx, loadout);
            OverridesGroupItems.AddReifiedDelete(tx, Refresh(loadout), overrides, ModFile);
            await tx.Commit();
        }
        ((ALoadoutSynchronizer)Synchronizer).WinningFilesForTest(Refresh(loadout))
            .Should().Contain(row => row.Path == ModFile, "the delete marker wins its path");

        // Scoped to the group, as the External Changes page does: an unscoped request refuses a recorded delete.
        await Remover.Remove(new FileRemovalRequest(loadout.LoadoutId, [ModFile], [LoadoutItemGroupId.From(overrides.Value)]), includeLocked: true);

        ((ALoadoutSynchronizer)Synchronizer).WinningFilesForTest(Refresh(loadout))
            .Should().NotContain(row => row.Path == ModFile, "the path no longer counts as a winning delete");
    }
}

/// <summary>
/// A mod that replaces a file the game ships: removing the mod's row hands the path back to the game's own file,
/// which the next Apply writes again.
/// </summary>
public class RemovedGameFileOverrideTests(ITestOutputHelper helper) : ASteamInstalledGameTest<RemovedGameFileOverrideTests, Cyberpunk2077Game>(helper)
{
    private const string Vanilla = "bin/x64/Cyberpunk2077.exe";
    private static readonly GamePath VanillaPath = new(LocationId.Game, Vanilla);

    protected override bool StoreVouchesForTheFiles => true;

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

    protected override SteamInstall InstallGame(SteamLibraryFixture library) => CyberpunkSteamInstall.Install(library);

    [Fact]
    public async Task RemovingAModsOverrideOfAGameFileRestoresTheGameFile()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)Vanilla], loadout.LoadoutId, "Mod over vanilla");
            await tx.Commit();
        }
        await Synchronizer.Synchronize(Refresh(loadout));
        var onDisk = loadout.InstallationInstance.Locations.ToAbsolutePath(VanillaPath);
        (await onDisk.ReadAllTextAsync()).Should().Be(Vanilla, "the mod's bytes are the path text");

        await ServiceProvider.GetRequiredService<ILoadoutFileRemover>()
            .Remove(new FileRemovalRequest(loadout.LoadoutId, [VanillaPath]), includeLocked: true);
        await Synchronizer.Synchronize(Refresh(loadout));

        (await onDisk.ReadAllTextAsync()).Should().Be("game executable", "the game's own file is back");
        ((ALoadoutSynchronizer)Synchronizer).WinningFilesForTest(Refresh(loadout))
            .Should().Contain(row => row.Path == VanillaPath && row.ItemType == "Game", "the path never left the winning files");
    }
}

