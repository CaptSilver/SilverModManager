using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.DataModel.Tests.Deployment;

public class DeploymentPlanStoreTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<DeploymentPlanStoreTests>(helper)
{
    private static readonly DeploymentBackendId Other = new("x");

    private IDeploymentPlanStore Store => ServiceProvider.GetRequiredService<IDeploymentPlanStore>();

    private async Task<GameInstallMetadataId> Manage()
    {
        var metadata = await LoadoutManager.ManageInstallation(GameInstallation);
        return metadata.Id;
    }

    private HashSet<EntityId> EntryIds() => DeploymentPlanEntry.All(Connection.Db).Select(e => e.Id).ToHashSet();

    [Fact]
    public async Task LoadWithoutRowsIsEmptyAndIncomplete()
    {
        var id = await Manage();

        var plan = Store.Load(Connection.Db, id);

        plan.Roots.Should().BeEmpty();
        plan.IsComplete(GameInstallation.Locations).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureCompleteWritesCopyRowsForEveryLocation()
    {
        var id = await Manage();

        using var tx = Connection.BeginTransaction();
        var plan = Store.EnsureComplete(tx, Connection.Db, GameInstallation, id);
        await tx.Commit();

        plan.IsComplete(GameInstallation.Locations).Should().BeTrue();
        var entries = DeploymentPlanEntry.All(Connection.Db).ToArray();
        entries.Select(e => ((GamePath)e.Root).LocationId).Should().BeEquivalentTo(GameInstallation.Locations.Keys);
        entries.Should().OnlyContain(e => e.Backend == "copy" && ((GamePath)e.Root).Path == RelativePath.Empty);
        Store.Load(Connection.Db, id).IsComplete(GameInstallation.Locations).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureCompleteTwiceKeepsTheSameEntities()
    {
        var id = await Manage();

        using (var tx = Connection.BeginTransaction())
        {
            Store.EnsureComplete(tx, Connection.Db, GameInstallation, id);
            await tx.Commit();
        }
        var before = EntryIds();

        using (var tx = Connection.BeginTransaction())
        {
            Store.EnsureComplete(tx, Connection.Db, GameInstallation, id);
            await tx.Commit();
        }

        before.Should().NotBeEmpty();
        EntryIds().Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task WriteRoutesOnlyTheGivenRoot()
    {
        var id = await Manage();
        var mods = new GamePath(LocationId.Game, "mods");

        using var tx = Connection.BeginTransaction();
        Store.Write(tx, id, mods, Other);
        await tx.Commit();

        var plan = Store.Load(Connection.Db, id);
        plan.BackendFor(new GamePath(LocationId.Game, "mods/a.txt")).Should().Be(Other);
        plan.BackendFor(new GamePath(LocationId.Game, "other/a.txt")).Should().Be(DeploymentBackendId.Copy);
    }

    [Fact]
    public async Task WriteAgainReplacesTheBackendOnTheSameEntity()
    {
        var id = await Manage();
        var mods = new GamePath(LocationId.Game, "mods");

        using (var tx = Connection.BeginTransaction())
        {
            Store.Write(tx, id, mods, Other);
            await tx.Commit();
        }
        using (var tx = Connection.BeginTransaction())
        {
            Store.Write(tx, id, mods, DeploymentBackendId.Copy);
            await tx.Commit();
        }

        EntryIds().Should().HaveCount(1);
        Store.Load(Connection.Db, id).BackendFor(new GamePath(LocationId.Game, "mods/a.txt")).Should().Be(DeploymentBackendId.Copy);
        Store.Load(Connection.Db, id).Roots.Should().ContainSingle();
    }

    [Fact]
    public async Task RowsOfOtherInstallationsAreIgnored()
    {
        var id = await Manage();
        var otherId = GameInstallMetadataId.From(EntityId.From(id.Value.Value + 1));

        using var tx = Connection.BeginTransaction();
        Store.Write(tx, otherId, new GamePath(LocationId.Game, "mods"), Other);
        await tx.Commit();

        Store.Load(Connection.Db, id).Roots.Should().BeEmpty();
    }
}
