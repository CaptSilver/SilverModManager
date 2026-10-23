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
    public async Task EnsureCompleteWritesTheSelectorsChoiceForEveryMissingLocation()
    {
        var id = await Manage();
        var selector = new FakeSelector(root => root.LocationId == LocationId.Game
            ? new DeploymentChoice(DeploymentBackendId.Hardlink, "same device")
            : new DeploymentChoice(DeploymentBackendId.Copy, "default"));

        using var tx = Connection.BeginTransaction();
        var plan = Store.EnsureComplete(tx, Connection.Db, GameInstallation, id, selector);
        await tx.Commit();

        plan.IsComplete(GameInstallation.Locations).Should().BeTrue();
        var entries = DeploymentPlanEntry.All(Connection.Db).ToArray();
        entries.Select(e => ((GamePath)e.Root).LocationId).Should().BeEquivalentTo(GameInstallation.Locations.Keys);
        foreach (var entry in entries)
        {
            var isGame = ((GamePath)entry.Root).LocationId == LocationId.Game;
            entry.Backend.Should().Be(isGame ? "hardlink" : "copy");
            entry.Reason.Value.Should().Be(isGame ? "same device" : "default");
            entry.Chosen.HasValue.Should().BeFalse();
            ((GamePath)entry.Root).Path.Should().Be(RelativePath.Empty);
        }
        plan.Roots.Single(r => r.Root.LocationId == LocationId.Game).Should().Be(
            new DeploymentPlanRoot(new GamePath(LocationId.Game, RelativePath.Empty), DeploymentBackendId.Hardlink, Chosen: false, Reason: "same device"));
        Store.Load(Connection.Db, id).Roots.Should().BeEquivalentTo(plan.Roots);
    }

    [Fact]
    public async Task EnsureCompleteWithTheCopyOnlySelectorStillWritesCopy()
    {
        var id = await Manage();

        using var tx = Connection.BeginTransaction();
        var plan = Store.EnsureComplete(tx, Connection.Db, GameInstallation, id, new FixedSelector(DeploymentBackendId.Copy));
        await tx.Commit();

        plan.IsComplete(GameInstallation.Locations).Should().BeTrue();
        var entries = DeploymentPlanEntry.All(Connection.Db).ToArray();
        entries.Select(e => ((GamePath)e.Root).LocationId).Should().BeEquivalentTo(GameInstallation.Locations.Keys);
        entries.Should().OnlyContain(e => e.Backend == "copy" && !e.Chosen.HasValue && ((GamePath)e.Root).Path == RelativePath.Empty);
        Store.Load(Connection.Db, id).IsComplete(GameInstallation.Locations).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureCompleteLeavesRecordedRowsAlone()
    {
        var id = await Manage();
        var game = new GamePath(LocationId.Game, RelativePath.Empty);
        using (var tx = Connection.BeginTransaction())
        {
            Store.Write(tx, Connection.Db, id, game, Other, chosen: true);
            await tx.Commit();
        }

        using (var tx = Connection.BeginTransaction())
        {
            Store.EnsureComplete(tx, Connection.Db, GameInstallation, id, new FixedSelector(DeploymentBackendId.Hardlink));
            await tx.Commit();
        }

        var plan = Store.Load(Connection.Db, id);
        plan.BackendFor(game).Should().Be(Other);
        plan.Roots.Single(r => r.Root == game).Chosen.Should().BeTrue();
    }

    [Fact]
    public async Task WriteMarksTheRowChosen()
    {
        var id = await Manage();
        var mods = new GamePath(LocationId.Game, "mods");

        using (var tx = Connection.BeginTransaction())
        {
            Store.Write(tx, Connection.Db, id, mods, Other, chosen: true);
            await tx.Commit();
        }

        Store.Load(Connection.Db, id).Roots.Should().ContainSingle().Which.Chosen.Should().BeTrue();
    }

    [Fact]
    public async Task WritingUnchosenRetractsTheMarkOfAChosenRow()
    {
        var id = await Manage();
        var mods = new GamePath(LocationId.Game, "mods");
        using (var tx = Connection.BeginTransaction())
        {
            Store.Write(tx, Connection.Db, id, mods, Other, chosen: true);
            await tx.Commit();
        }

        using (var tx = Connection.BeginTransaction())
        {
            Store.Write(tx, Connection.Db, id, mods, DeploymentBackendId.Copy, chosen: false);
            await tx.Commit();
        }

        var entry = DeploymentPlanEntry.All(Connection.Db).Should().ContainSingle().Subject;
        entry.Chosen.HasValue.Should().BeFalse();
        Store.Load(Connection.Db, id).Roots.Should().ContainSingle().Which.Chosen.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureCompleteThenWriteInOneTransactionYieldsOneEntity()
    {
        var id = await Manage();
        var game = new GamePath(LocationId.Game, RelativePath.Empty);

        using (var tx = Connection.BeginTransaction())
        {
            Store.EnsureComplete(tx, Connection.Db, GameInstallation, id, new FixedSelector(DeploymentBackendId.Copy));
            Store.Write(tx, Connection.Db, id, game, Other, chosen: true);
            await tx.Commit();
        }

        DeploymentPlanEntry.All(Connection.Db).Count(e => ((GamePath)e.Root).LocationId == LocationId.Game).Should().Be(1);
        Store.Load(Connection.Db, id).BackendFor(game).Should().Be(Other);
    }

    [Fact]
    public async Task RootsDifferingOnlyInCaseAreOneRow()
    {
        var id = await Manage();

        using (var tx = Connection.BeginTransaction())
        {
            Store.Write(tx, Connection.Db, id, new GamePath(LocationId.Game, "Mods"), Other, chosen: false);
            await tx.Commit();
        }
        using (var tx = Connection.BeginTransaction())
        {
            Store.Write(tx, Connection.Db, id, new GamePath(LocationId.Game, "mods"), DeploymentBackendId.Copy, chosen: false);
            await tx.Commit();
        }

        Store.Load(Connection.Db, id).Roots.Should().ContainSingle();
        Store.Load(Connection.Db, id).BackendFor(new GamePath(LocationId.Game, "MODS/a.txt")).Should().Be(DeploymentBackendId.Copy);
    }

    [Fact]
    public async Task EnsureCompleteTwiceKeepsTheSameEntities()
    {
        var id = await Manage();

        using (var tx = Connection.BeginTransaction())
        {
            Store.EnsureComplete(tx, Connection.Db, GameInstallation, id, new FixedSelector(DeploymentBackendId.Copy));
            await tx.Commit();
        }
        var before = EntryIds();

        using (var tx = Connection.BeginTransaction())
        {
            Store.EnsureComplete(tx, Connection.Db, GameInstallation, id, new FixedSelector(DeploymentBackendId.Copy));
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
        Store.Write(tx, Connection.Db, id, mods, Other, chosen: false);
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
            Store.Write(tx, Connection.Db, id, mods, Other, chosen: false);
            await tx.Commit();
        }
        using (var tx = Connection.BeginTransaction())
        {
            Store.Write(tx, Connection.Db, id, mods, DeploymentBackendId.Copy, chosen: false);
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
        Store.Write(tx, Connection.Db, otherId, new GamePath(LocationId.Game, "mods"), Other, chosen: false);
        await tx.Commit();

        Store.Load(Connection.Db, id).Roots.Should().BeEmpty();
    }

    private sealed class FakeSelector(Func<GamePath, DeploymentChoice> choose) : IDeploymentBackendSelector
    {
        public DeploymentChoice Choose(GameInstallation installation, GamePath root) => choose(root);
    }

    private sealed class FixedSelector(DeploymentBackendId backend) : IDeploymentBackendSelector
    {
        public DeploymentChoice Choose(GameInstallation installation, GamePath root) => new(backend, "pinned");
    }
}
