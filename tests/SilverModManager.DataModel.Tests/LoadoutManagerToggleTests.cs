using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.DataModel.Undo;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

public class LoadoutManagerToggleTests(ITestOutputHelper helper) : ANexusCollectionTest<LoadoutManagerToggleTests>(helper)
{
    private UndoService UndoService => ServiceProvider.GetRequiredService<UndoService>();

    private async Task<(LoadoutId Loadout, CollectionGroupId Mods)> Seed()
    {
        var loadout = await CreateLoadout();
        return (loadout, DefaultCollection.Of(Connection.Db, loadout)!.Value);
    }

    private async Task<LoadoutItemGroupId> AddMod(LoadoutId loadout, string name, CollectionGroupId parent)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout, name, LoadoutItemGroupId.From(parent.Value));
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group].Value);
    }

    private bool IsDisabled(LoadoutItemGroupId group) => LoadoutItem.Load(Connection.Db, LoadoutItemId.From(group.Value)).IsDisabled;

    private static LoadoutItemId Item(LoadoutItemGroupId group) => LoadoutItemId.From(group.Value);

    [Fact]
    public async Task SetGroupsEnabled_WritesARestorePointBeforeTheChange()
    {
        var (loadout, mods) = await Seed();
        var mod = await AddMod(loadout, "Mod", mods);
        var revisionsBefore = UndoService.RevisionsFor(loadout.Value).Count;

        var result = await LoadoutManager.SetGroupsEnabled(loadout, [Item(mod)], enabled: false);

        result.Changed.Should().Equal(Item(mod));
        result.Skipped.Should().BeEmpty();
        IsDisabled(mod).Should().BeTrue();
        var revisions = UndoService.RevisionsFor(loadout.Value);
        revisions.Count.Should().Be(revisionsBefore + 1, "the restore point is a revision");

        await UndoService.RevertTo(revisions.OrderBy(r => r.Revision.TxEntity).Last().Revision);
        IsDisabled(mod).Should().BeFalse("reverting to the restore point brings the switch back");
    }

    [Fact]
    public async Task SetGroupsEnabled_EnablingRetractsTheMarker()
    {
        var (loadout, mods) = await Seed();
        var mod = await AddMod(loadout, "Mod", mods);
        await LoadoutManager.SetGroupsEnabled(loadout, [Item(mod)], enabled: false);

        await LoadoutManager.SetGroupsEnabled(loadout, [Item(mod)], enabled: true);

        IsDisabled(mod).Should().BeFalse();
    }

    [Fact]
    public async Task SetGroupsEnabled_SkipsRequiredCollectionItems()
    {
        var (loadout, mods) = await Seed();
        var required = await AddMod(loadout, "Required", mods);
        var optional = await AddMod(loadout, "Optional", mods);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(required.Value, NexusCollectionItemLoadoutGroup.IsRequired, true);
            await tx.Commit();
        }

        var result = await LoadoutManager.SetGroupsEnabled(loadout, [Item(required), Item(optional)], enabled: false);

        result.Changed.Should().Equal(Item(optional));
        result.Skipped.Should().ContainSingle().Which.Id.Should().Be(Item(required));
        IsDisabled(required).Should().BeFalse();
        IsDisabled(optional).Should().BeTrue();
    }

    [Fact]
    public async Task SetGroupsEnabled_NothingToChange_WritesNothing()
    {
        var (loadout, mods) = await Seed();
        var mod = await AddMod(loadout, "Mod", mods);
        var before = Connection.Db.BasisTxId;

        var result = await LoadoutManager.SetGroupsEnabled(loadout, [Item(mod)], enabled: true);

        result.Changed.Should().BeEmpty();
        Connection.Db.BasisTxId.Should().Be(before);
    }
}
