using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests.Deployment;

public class DeploymentLaunchRootsTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<DeploymentLaunchRootsTests>(helper)
{
    private static readonly GamePath GameRoot = new(LocationId.Game, RelativePath.Empty);
    private static readonly GamePath ModsRoot = new(LocationId.Game, "mods");
    private static readonly GamePath OtherRoot = new(LocationId.Game, "other");

    private static readonly AbsolutePath Store = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/run/media/store");
    private static readonly AbsolutePath OtherFolder = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/run/media/other");

    private IDeploymentPlanStore Plans => ServiceProvider.GetRequiredService<IDeploymentPlanStore>();

    private async Task<GameInstallMetadataId> Route(params (GamePath Root, DeploymentBackendId Backend)[] rows)
    {
        var metadata = await LoadoutManager.ManageInstallation(GameInstallation);
        using var tx = Connection.BeginTransaction();
        foreach (var (root, backend) in rows) Plans.Write(tx, Connection.Db, metadata.Id, root, backend, chosen: false);
        await tx.Commit();
        return metadata.Id;
    }

    private DeploymentLaunchRoots Roots(params IDeploymentBackend[] backends) => new(Plans, backends, Connection);

    [Fact]
    public async Task CopyAndHardlinkRootsNeedNothing()
    {
        var id = await Route((GameRoot, DeploymentBackendId.Copy), (ModsRoot, DeploymentBackendId.Hardlink));

        var roots = Roots(ServiceProvider.GetRequiredService<CopyDeploymentBackend>(), new NeedsFolders(DeploymentBackendId.Hardlink));

        roots.For(GameInstallation, id).Should().BeEmpty();
    }

    [Fact]
    public async Task ASymlinkRootNeedsTheStoreRootOnce()
    {
        var id = await Route((GameRoot, DeploymentBackendId.Symlink), (ModsRoot, DeploymentBackendId.Symlink));

        var roots = Roots(new NeedsFolders(DeploymentBackendId.Symlink, Store));

        roots.For(GameInstallation, id).Should().Equal(Store.ToString());
    }

    [Fact]
    public async Task FoldersFromDifferentBackendsAreUnionedInOrdinalOrder()
    {
        var id = await Route((GameRoot, DeploymentBackendId.Symlink), (ModsRoot, DeploymentBackendId.Reflink), (OtherRoot, DeploymentBackendId.Symlink));

        var roots = Roots(new NeedsFolders(DeploymentBackendId.Symlink, Store), new NeedsFolders(DeploymentBackendId.Reflink, OtherFolder, Store));

        roots.For(GameInstallation, id).Should().Equal(OtherFolder.ToString(), Store.ToString());
    }

    [Fact]
    public async Task AnUnregisteredBackendInThePlanIsSkipped()
    {
        var id = await Route((GameRoot, DeploymentBackendId.Symlink));

        var roots = Roots(ServiceProvider.GetRequiredService<CopyDeploymentBackend>());

        roots.For(GameInstallation, id).Should().BeEmpty();
    }

    private sealed class NeedsFolders(DeploymentBackendId id, params AbsolutePath[] folders) : IDeploymentBackend
    {
        public DeploymentBackendId Id => id;
        public bool UsesLooseStaging => false;
        public DeploymentPreflight Preflight(GameInstallation installation, GamePath root) => DeploymentPreflight.Accept("test");
        public IReadOnlyList<AbsolutePath> RequiredFilesystems(GameInstallation installation, GamePath root) => folders;

        public Task Backup(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public void Ingest(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public Task AdaptLoadout(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public IReadOnlyCollection<GamePath> Delete(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public Task Extract(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public Task WriteIntrinsics(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public void AddReifiedDeletes(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();
        public void CleanDirectories(DeploymentContext context, IReadOnlyCollection<GamePath> directoriesWithDeletions, Entities<DiskStateEntry.ReadOnly> newDiskState) => throw new NotSupportedException();
    }
}
