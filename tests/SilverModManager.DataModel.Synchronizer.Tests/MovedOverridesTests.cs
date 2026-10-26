using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A file the user changed in the game folder lands in the Overrides group. Once it is moved into a mod, the
/// next sync has to treat it as that mod's file: nothing to do on disk, and no new Overrides row.
/// </summary>
public class MovedOverridesTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<MovedOverridesTests>(helper)
{
    private static readonly GamePath External = new(LocationId.Game, "archive/pc/mod/external.archive");

    private IOverridesMover Mover => ServiceProvider.GetRequiredService<IOverridesMover>();

    private IEnumerable<LoadoutItemWithTargetPath.ReadOnly> RowsAt(Loadout.ReadOnly loadout) =>
        LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath().Where(r => r.TargetPath == External);

    private async Task<(Loadout.ReadOnly Loadout, LoadoutItemGroupId Mod)> LoadoutWithExternalFileMovedToAMod()
    {
        var loadout = await CreateLoadout();
        var disk = loadout.InstallationInstance.Locations.ToAbsolutePath(External);
        disk.Parent.CreateDirectory();
        await disk.WriteAllTextAsync("external");
        loadout = await Synchronizer.Synchronize(loadout);
        RowsAt(loadout).Should().ContainSingle().Which.AsLoadoutItem().Parent.AsLoadoutItem().Name.Should().Be("Overrides");

        var mod = await Mover.CreateEmptyGroup(loadout.LoadoutId, "Kept edits");
        await Mover.Move(loadout.LoadoutId, [External], mod, new MoveOptions());
        return (Refresh(loadout), mod);
    }

    [Fact]
    public async Task TheSyncAfterAMoveHasNothingToDoForTheMovedFile()
    {
        var (loadout, _) = await LoadoutWithExternalFileMovedToAMod();

        var tree = await Synchronizer.BuildSyncTree(loadout);
        Synchronizer.ProcessSyncTree(tree);

        tree[External].Actions.Should().Be(Actions.DoNothing, "disk, previous state and loadout all hold the same content");
        tree[External].SourceItemType.Should().Be(LoadoutSourceItemType.Loadout);
    }

    [Fact]
    public async Task AFileMovedIntoAModStaysThereAcrossSyncs()
    {
        var (loadout, mod) = await LoadoutWithExternalFileMovedToAMod();
        var rowId = RowsAt(loadout).Single().Id;

        loadout = await Synchronizer.Synchronize(loadout);
        loadout = await Synchronizer.Synchronize(loadout);

        var row = RowsAt(loadout).Should().ContainSingle("the file must not be ingested a second time").Subject;
        row.Id.Should().Be(rowId);
        row.AsLoadoutItem().ParentId.Value.Should().Be(mod.Value);
        var disk = loadout.InstallationInstance.Locations.ToAbsolutePath(External);
        (await disk.ReadAllTextAsync()).Should().Be("external");
        row.TryGetAsLoadoutFile(out var file).Should().BeTrue();
        file.Hash.Should().Be("external".xxHash3AsUtf8());
    }

    [Fact]
    public async Task ADisabledModTakesTheMovedFileOffDiskAndEnablingBringsItBack()
    {
        var (loadout, mod) = await LoadoutWithExternalFileMovedToAMod();
        var disk = loadout.InstallationInstance.Locations.ToAbsolutePath(External);

        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(mod.Value, LoadoutItem.Disabled, SilverModManager.MnemonicDB.Abstractions.ElementComparers.Null.Instance);
            await tx.Commit();
        }
        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        disk.FileExists.Should().BeFalse("the file is the mod's now, so disabling the mod removes it");

        using (var tx = Connection.BeginTransaction())
        {
            tx.Retract(mod.Value, LoadoutItem.Disabled, SilverModManager.MnemonicDB.Abstractions.ElementComparers.Null.Instance);
            await tx.Commit();
        }
        await Synchronizer.Synchronize(Refresh(loadout));
        (await disk.ReadAllTextAsync()).Should().Be("external");
    }
}
