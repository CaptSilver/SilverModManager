using System.Collections.Frozen;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Collections.Tests.LoadOrder;

/// <summary>
/// A collection installed while the order matcher was wrong keeps a scrambled order and a list of entries it dropped.
/// Applying the order again has to fix both from the manifest alone.
/// </summary>
public class CollectionOrderReapplyTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CollectionOrderReapplyTests>(helper)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // Winner first, as the manifest writes it: the last mod listed is the first one loaded.
    private static SilverLoadOrderEntry[] WinnerFirst(IReadOnlyList<Mod> mods) =>
        [..new[] { 2, 0, 1 }.Select(i => new SilverLoadOrderEntry { Md5 = mods[i].Source.Md5, Name = mods[i].Name })];

    private async Task<(Loadout.ReadOnly Loadout, NexusCollectionLoadoutGroup.ReadOnly Group, string[] Expected)> InstallCollection()
    {
        var mods = Enumerable.Range(0, 3).Select(i => ((SilverModManager.Games.FOMOD.FomodOption[]?)null, $"Plain mod {i}")).ToArray();
        var (file, revision, items) = await new OfflineCollections(ServiceProvider, Connection, LibraryService, TemporaryFileManager)
            .Create(mods, silverLoadOrder: WinnerFirst);
        var loadout = await CreateLoadout();

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        async Task<NexusCollectionLoadoutGroup.ReadOnly> Run() => await job;
        var group = await Run().WaitAsync(Timeout);

        var expected = Names(group);
        expected.Should().HaveCount(3);
        expected.Select(name => name.Last().ToString()).Should().Equal("2", "0", "1");
        return (loadout, group, expected);
    }

    private string[] Names(NexusCollectionLoadoutGroup.ReadOnly group) =>
        ContainerOrder.ChildrenWinnerFirst(Connection.Db, group.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId)
            .Select(child => child.AsLoadoutItem().Name.Replace(".zip", "").Replace("mod", "Plain mod ")).ToArray();

    private async Task Scramble(Loadout.ReadOnly loadout, NexusCollectionLoadoutGroup.ReadOnly group)
    {
        // What the broken joiner left behind: the mods in the wrong order and an entry per mod that matched nothing.
        var container = group.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId;
        var reversed = ContainerOrder.ChildrenWinnerFirst(Connection.Db, container).Reverse().Select(child => new ModOrderEntry(child.LoadoutItemGroupId, null));
        var drops = Enumerable.Range(0, 5).Select(i => new ModOrderDrop($"modId{i}", $"no mod in the collection matches 'modId{i}'", "Load Order"));

        using var tx = Connection.BeginTransaction();
        new ModOrderImporter(ServiceProvider, Connection, NullLogger<ModOrderImporter>.Instance)
            .Import(Refresh(loadout), ModOrder.Create(reversed, drops), new ModOrderSource($"collection {group.Collection.Name}", container, Manifest: null), tx);
        await tx.Commit();
    }

    private ModOrderDroppedEntry.ReadOnly[] Rows(LoadoutId loadout, NexusCollectionLoadoutGroup.ReadOnly group) =>
        ModOrderDroppedEntry.FindByLoadout(Connection.Db, loadout).Where(row => row.ContainerId == group.AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId).ToArray();

    private async Task<Diagnostic[]> Cards(Loadout.ReadOnly loadout)
    {
        var cards = new List<Diagnostic>();
        await foreach (var card in new ModOrderDroppedEmitter().Diagnose(Loadout.Load(Connection.Db, loadout.LoadoutId), FrozenDictionary<GamePath, SyncNode>.Empty, CancellationToken.None))
            cards.Add(card);
        return cards.ToArray();
    }

    [Fact]
    public async Task ReapplyingReplacesTheDroppedRowsAndRestoresTheOrder()
    {
        var (loadout, group, expected) = await InstallCollection();
        await Scramble(loadout, group);
        Names(group).Should().Equal(expected.Reverse(), "the scramble put the mods in the wrong order");
        Rows(loadout.LoadoutId, group).Should().HaveCount(5);
        var before = await Cards(loadout);
        before.Should().ContainSingle().Which.Actions.Should().ContainSingle(action => action.Id == DiagnosticActions.ReapplyCollectionOrderId);

        var result = await CollectionOrderReapply.Reapply(ServiceProvider, group);

        result.Dropped.Should().Be(0);
        Rows(loadout.LoadoutId, group).Should().BeEmpty();
        Names(group).Should().Equal(expected, "the manifest lists the winner first, so the winner sits last in the load order");
        (await Cards(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReapplyingTwiceChangesNothingTheSecondTime()
    {
        var (loadout, group, expected) = await InstallCollection();
        await Scramble(loadout, group);
        await CollectionOrderReapply.Reapply(ServiceProvider, group);

        await CollectionOrderReapply.Reapply(ServiceProvider, group);

        Names(group).Should().Equal(expected);
        Rows(loadout.LoadoutId, group).Should().BeEmpty();
    }

    [Fact]
    public async Task ReapplyingRefusesWhileTheGameIsRunningAndChangesNothing()
    {
        var (loadout, group, expected) = await InstallCollection();
        await Scramble(loadout, group);
        ServiceProvider.GetRequiredService<FakeGameLaunchSession>().Running = true;

        var act = async () => await CollectionOrderReapply.Reapply(ServiceProvider, group);

        (await act.Should().ThrowAsync<CollectionOrderReapplyRefusedException>()).Which.Message.Should().Contain("running");
        Rows(loadout.LoadoutId, group).Should().HaveCount(5);
        Names(group).Should().Equal(expected.Reverse());
    }

    [Fact]
    public async Task ReapplyingRefusesWhileTheCollectionIsBeingUpdated()
    {
        var (_, group, _) = await InstallCollection();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var holder = CollectionUpdater.RunExclusive(ServiceProvider, group, async () =>
        {
            entered.SetResult();
            await release.Task;
        }, CancellationToken.None);
        await entered.Task;

        try
        {
            var act = async () => await CollectionOrderReapply.Reapply(ServiceProvider, group);
            (await act.Should().ThrowAsync<CollectionOrderReapplyRefusedException>()).Which.Message.Should().Contain("updated");
        }
        finally
        {
            release.SetResult();
            await holder;
        }
    }
}
