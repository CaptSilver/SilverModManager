using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// One scope per run mounts every mounting root of the recorded plan once the apply is done, and finishes them in
/// the opposite order before the ingest.
/// </summary>
public class DeploymentMountScopeTests(ITestOutputHelper helper) : AStubbedGameTest<DeploymentMountScopeTests>(helper)
{
    private static readonly GamePath DataRoot = new(LocationId.Game, "Data");
    private static readonly GamePath PluginsRoot = new(LocationId.Game, "Plugins");

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddRecordingMountingDeploymentBackend();

    private RecordingMountingDeploymentBackend Mounting => ServiceProvider.GetRequiredService<RecordingMountingDeploymentBackend>();

    private HeldMounts Held => ServiceProvider.GetRequiredService<HeldMounts>();

    private IDeploymentMountScope NewScope() => ServiceProvider.GetRequiredService<IDeploymentMountScopeFactory>().Create();

    private async Task<Loadout.ReadOnly> LoadoutWithMountingRoots(params GamePath[] roots)
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var id = GameRegistry.ForceGetMetadata(GameInstallation).Id;
        using var tx = Connection.BeginTransaction();
        foreach (var root in roots)
            ServiceProvider.GetRequiredService<IDeploymentPlanStore>().Write(tx, Connection.Db, id, root, RecordingMountingDeploymentBackend.BackendId, chosen: true);
        await tx.Commit();
        return await CreateLoadout();
    }

    private IEnumerable<GamePath> PlannedMountingRoots() =>
        ServiceProvider.GetRequiredService<IDeploymentPlanStore>()
            .Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id).Roots
            .Where(r => r.Backend == RecordingMountingDeploymentBackend.BackendId).Select(r => r.Root);

    [Fact]
    public async Task MountsEveryMountingRootOfThePlanInOrder()
    {
        var loadout = await LoadoutWithMountingRoots(DataRoot, PluginsRoot);
        await using var scope = NewScope();

        await scope.Mount(GameInstallation, loadout, CancellationToken.None);

        var expected = PlannedMountingRoots().ToList();
        expected.Should().HaveCount(2);
        Mounting.Calls.Where(c => c.Phase == "Mount").Select(c => c.Path).Should().Equal(expected);
        scope.MountedRoots.Should().Equal(expected);
    }

    [Fact]
    public async Task FinishRunsInReverseOrderAndReturnsEveryResult()
    {
        var loadout = await LoadoutWithMountingRoots(DataRoot, PluginsRoot);
        Mounting.FinishResults[DataRoot] = new MountFinishResult(MountFinishStatus.Promoted, [RelativePath.FromUnsanitizedInput("a.esp")]);
        await using var scope = NewScope();
        await scope.Mount(GameInstallation, loadout, CancellationToken.None);

        var results = await scope.Finish(CancellationToken.None);

        var expected = PlannedMountingRoots().ToList();
        Mounting.Calls.Where(c => c.Phase == "Finish").Select(c => c.Path).Should().Equal(Enumerable.Reverse(expected));
        results.Should().HaveCount(2);
        results.Should().ContainSingle(r => r.Status == MountFinishStatus.Promoted);
    }

    [Fact]
    public async Task AFailingMountFinishesTheOnesAlreadyUpThenThrows()
    {
        var loadout = await LoadoutWithMountingRoots(DataRoot, PluginsRoot);
        var order = PlannedMountingRoots().ToList();
        Mounting.FailingMounts.Add(order[1]);
        await using var scope = NewScope();

        var act = () => scope.Mount(GameInstallation, loadout, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        Mounting.Calls.Where(c => c.Phase == "Finish").Select(c => c.Path).Should().Equal(order[0]);
        scope.MountedRoots.Should().BeEmpty();
        Held.Any.Should().BeFalse();
    }

    [Fact]
    public async Task HeldMountsIsEmptyAfterFinish()
    {
        var loadout = await LoadoutWithMountingRoots(DataRoot);
        await using var scope = NewScope();
        await scope.Mount(GameInstallation, loadout, CancellationToken.None);

        Held.Any.Should().BeTrue();
        Held.Current.Should().ContainSingle().Which.Root.Should().Be(DataRoot);

        await scope.Finish(CancellationToken.None);

        Held.Any.Should().BeFalse();
    }

    [Fact]
    public async Task DisposeFinishesWhatFinishDidNot()
    {
        var loadout = await LoadoutWithMountingRoots(DataRoot);
        var scope = NewScope();
        await scope.Mount(GameInstallation, loadout, CancellationToken.None);

        await scope.DisposeAsync();

        Mounting.Calls.Should().Contain(("Finish", DataRoot));
        Held.Any.Should().BeFalse();
    }

    [Fact]
    public async Task APlanWithoutAMountingRootMountsNothing()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();
        await using var scope = NewScope();

        await scope.Mount(GameInstallation, loadout, CancellationToken.None);

        scope.MountedRoots.Should().BeEmpty();
        Mounting.Calls.Should().NotContain(c => c.Phase == "Mount");
    }
}
