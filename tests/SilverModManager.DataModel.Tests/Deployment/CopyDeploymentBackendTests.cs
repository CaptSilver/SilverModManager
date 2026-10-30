using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;

namespace SilverModManager.DataModel.Tests.Deployment;

public class CopyDeploymentBackendTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<CopyDeploymentBackendTests>(helper)
{
    [Fact]
    public void RegisteredBackendsAreCopyReflinkHardlinkAndSymlink()
    {
        var backends = ServiceProvider.GetServices<IDeploymentBackend>().ToArray();

        backends.Select(b => b.Id).Should().BeEquivalentTo(
            [DeploymentBackendId.Copy, DeploymentBackendId.Reflink, DeploymentBackendId.Hardlink, DeploymentBackendId.Symlink]);
        backends.Single(b => b.Id == DeploymentBackendId.Copy).UsesLooseStaging.Should().BeFalse();
        backends.Where(b => b.Id != DeploymentBackendId.Copy).Should().OnlyContain(b => b.UsesLooseStaging);
    }

    [Fact]
    public async Task DeleteReturnsParentsOfFilesThatAreNotExtractedAgain()
    {
        var metadata = await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();

        var pathX = new GamePath(LocationId.Game, "a/x.txt");
        var pathY = new GamePath(LocationId.Game, "b/y.txt");

        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "Delete mod");
            var archived = new List<ArchivedFileEntry>();
            foreach (var path in new[] { pathX, pathY })
            {
                var content = $"content of {path}";
                AddFile(tx, loadout.LoadoutId, group, path, content, out var hash, out var size);
                archived.Add(new ArchivedFileEntry(new MemoryStreamFactory(path.FileName, new MemoryStream(Encoding.UTF8.GetBytes(content))), FileIdentity.XxHash3(hash), size));
            }
            await FileStore.BackupFiles(archived);
            await tx.Commit();
        }

        await Synchronizer.Synchronize(loadout);

        var absoluteX = GameInstallation.Locations.ToAbsolutePath(pathX);
        var absoluteY = GameInstallation.Locations.ToAbsolutePath(pathY);
        absoluteX.FileExists.Should().BeTrue();
        absoluteY.FileExists.Should().BeTrue();

        var disk = DiskStateEntry.FindByGame(Connection.Db, metadata.Id).ToDictionary(e => (GamePath)e.Path);
        SyncNode NodeFor(GamePath path, Actions actions)
        {
            var entry = disk[path];
            return new SyncNode
            {
                Disk = new SyncNodePart
                {
                    EntityId = entry.Id,
                    Identity = entry.IdentityOf(),
                    Size = entry.Size,
                    LastModifiedTicks = entry.LastModified.UtcTicks,
                },
                Actions = actions,
            };
        }

        var nodes = new Dictionary<GamePath, SyncNode>
        {
            [pathX] = NodeFor(pathX, Actions.DeleteFromDisk),
            [pathY] = NodeFor(pathY, Actions.DeleteFromDisk | Actions.ExtractToDisk),
        };

        var backend = ServiceProvider.GetServices<IDeploymentBackend>().Single(b => b.Id == DeploymentBackendId.Copy);

        // Uncommitted: the backend must only record into the transaction it is given.
        using var deleteTx = Connection.BeginTransaction();
        var context = new DeploymentContext
        {
            Synchronizer = (ALoadoutSynchronizer)Synchronizer,
            Installation = GameInstallation,
            MetadataId = metadata.Id,
            SyncTree = nodes,
            Transaction = deleteTx,
        };

        var parents = backend.Delete(context, nodes);

        parents.Should().BeEquivalentTo([pathX.Parent]);
        absoluteX.FileExists.Should().BeFalse();
        absoluteY.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task Backup_OfANodeWhoseDiskIdentityIsNotXxHash3_Throws()
    {
        var metadata = await LoadoutManager.ManageInstallation(GameInstallation);
        var path = new GamePath(LocationId.Game, "vanilla/depot.bin");
        var file = GameInstallation.Locations.ToAbsolutePath(path);
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync("vanilla");
        var nodes = new Dictionary<GamePath, SyncNode>
        {
            [path] = new()
            {
                Disk = new SyncNodePart
                {
                    Identity = FileIdentity.Sha1(Sha1Hasher.Hash("vanilla"u8)),
                    Size = Size.FromLong(7),
                    LastModifiedTicks = 1,
                },
                Actions = Actions.BackupFile,
            },
        };
        var backend = ServiceProvider.GetServices<IDeploymentBackend>().Single(b => b.Id == DeploymentBackendId.Copy);
        using var tx = Connection.BeginTransaction();
        var context = new DeploymentContext
        {
            Synchronizer = (ALoadoutSynchronizer)Synchronizer,
            Installation = GameInstallation,
            MetadataId = metadata.Id,
            SyncTree = nodes,
            Transaction = tx,
        };

        var act = async () => await backend.Backup(context, nodes);

        // No Debug.Assert in front of it: the throw is the guard, so nothing is archived under a key that is not its xxHash3.
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Sha1");
        (await FileStore.HaveFile(FileIdentity.XxHash3(Encoding.UTF8.GetBytes("vanilla").xxHash3()))).Should().BeFalse();
    }
}
