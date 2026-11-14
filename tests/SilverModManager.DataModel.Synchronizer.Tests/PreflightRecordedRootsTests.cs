using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A root recorded with a backend that stops working is refused before anything is written, and the message names the
/// command that moves it.
/// </summary>
public class PreflightRecordedRootsTests(ITestOutputHelper helper) : AStubbedGameTest<PreflightRecordedRootsTests>(helper)
{
    private static readonly GamePath GameRoot = new(LocationId.Game, RelativePath.Empty);

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddSingleton<IDeploymentBackend>(new RefusingBackend());

    private sealed class RefusingBackend : IDeploymentBackend
    {
        public static readonly DeploymentBackendId BackendId = new("test-refusing");

        public DeploymentBackendId Id => BackendId;

        public bool UsesLooseStaging => false;

        public DeploymentPreflight Preflight(GameInstallation installation, GamePath root) => DeploymentPreflight.Refuse("the drive went away");

        public IReadOnlyList<AbsolutePath> RequiredFilesystems(GameInstallation installation, GamePath root) => [];

        public Task Backup(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();

        public void Ingest(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();

        public Task AdaptLoadout(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();

        public IReadOnlyCollection<GamePath> Delete(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();

        public Task Extract(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();

        public Task WriteIntrinsics(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();

        public void AddReifiedDeletes(DeploymentContext context, IReadOnlyDictionary<GamePath, SyncNode> nodes) => throw new NotSupportedException();

        public void CleanDirectories(DeploymentContext context, IReadOnlyCollection<GamePath> directoriesWithDeletions, MnemonicDB.Abstractions.IndexSegments.Entities<DiskStateEntry.ReadOnly> newDiskState) => throw new NotSupportedException();
    }

    [Fact]
    public async Task NamesTheMoveCommandWithTheChainsPick()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "Mod");
            var path = new GamePath(LocationId.Game, "mods/a.txt");
            AddFile(tx, loadout.LoadoutId, group, path, "a content", out var identity, out var size);
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.FileName, new MemoryStream(Encoding.UTF8.GetBytes("a content"))), identity, size)]);
            ServiceProvider.GetRequiredService<IDeploymentPlanStore>().Write(tx, Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id, GameRoot, RefusingBackend.BackendId, chosen: true);
            await tx.Commit();
        }
        var pick = ServiceProvider.GetRequiredService<IDeploymentBackendSelector>().Choose(GameInstallation, GameRoot).Backend;

        var act = async () => await Synchronizer.Synchronize(Refresh(loadout));

        (await act.Should().ThrowAsync<SynchronizerPreflightException>()).Which.Message.Should().Contain("the drive went away")
            .And.Contain($"Run 'silvermodmanager deployment --game \"Stubbed Game\" --root Game --use {pick.Value}' to move it, then apply again.");
    }
}
