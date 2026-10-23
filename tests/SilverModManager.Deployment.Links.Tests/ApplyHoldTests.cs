using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Links.Tests.Fixtures;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Deployment.Links.Tests;

public class ApplyHoldTests(ITestOutputHelper helper)
    : LinkedGameTest<ApplyHoldTests>(helper, Placement.TempDirectory, Placement.TempDirectory, HoldObservingBackend.BackendId)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services).AddSingleton<IDeploymentBackend>(sp => new HoldObservingBackend(
            sp.GetRequiredService<CopyDeploymentBackend>(),
            sp.GetRequiredService<ILooseStagingStore>()
        ));
    }

    [Fact]
    public async Task AnApplyHoldsTheStagingStoreFromPlanningToTheEndOfTheExtract()
    {
        var loadout = await NewLoadout();
        await AddMod(loadout, "Mod", ("a/x.txt", "mod bytes"));
        var backend = ServiceProvider.GetServices<IDeploymentBackend>().OfType<HoldObservingBackend>().Single();

        await Synchronizer.Synchronize(loadout);

        backend.SweepFinishedDuringExtract.Should().BeFalse("a sweep must wait for the apply's hold");
        (await backend.Sweep.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// Starts a sweep from inside an extract and notes whether it got through while the apply was running.
    /// </summary>
    private sealed class HoldObservingBackend(CopyDeploymentBackend inner, ILooseStagingStore store) : IDeploymentBackend
    {
        public static readonly DeploymentBackendId BackendId = new("test-hold");

        public bool SweepFinishedDuringExtract { get; private set; }

        public Task<int> Sweep { get; private set; } = Task.FromResult(-1);

        public DeploymentBackendId Id => BackendId;
        public bool UsesLooseStaging => false;
        public DeploymentPreflight Preflight(GameInstallation installation, GamePath root) => DeploymentPreflight.Accept("observes the hold");
        public IReadOnlyList<AbsolutePath> RequiredFilesystems(GameInstallation installation, GamePath root) => [];
        public Task Backup(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.Backup(context, nodes);
        public void Ingest(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.Ingest(context, nodes);
        public Task AdaptLoadout(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.AdaptLoadout(context, nodes);
        public IReadOnlyCollection<GamePath> Delete(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.Delete(context, nodes);
        public Task WriteIntrinsics(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.WriteIntrinsics(context, nodes);
        public void AddReifiedDeletes(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => inner.AddReifiedDeletes(context, nodes);
        public void CleanDirectories(DeploymentContext context, IReadOnlyCollection<GamePath> directoriesWithDeletions, Entities<DiskStateEntry.ReadOnly> newDiskState)
            => inner.CleanDirectories(context, directoriesWithDeletions, newDiskState);

        public async Task Extract(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes)
        {
            Sweep = Task.Run(() => store.Sweep(() => new Dictionary<GameInstallMetadataId, IReadOnlySet<Hash>>()));
            await Task.Delay(500);
            SweepFinishedDuringExtract = Sweep.IsCompleted;
            await inner.Extract(context, nodes);
        }
    }
}
