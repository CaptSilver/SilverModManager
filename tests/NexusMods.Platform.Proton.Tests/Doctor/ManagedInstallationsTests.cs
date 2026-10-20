using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers.Deployment;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Doctor;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;

namespace NexusMods.Platform.Proton.Tests.Doctor;

/// <summary>
/// The temp directory is tmpfs on the dev box, where hard links work and FICLONE does not, so the chain's pick
/// does not depend on where the checkout lives.
/// </summary>
public class ManagedInstallationsTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<ManagedInstallationsTests>(helper)
{
    private static readonly AbsolutePath Folder = NexusMods.Paths.FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine($"managed-installations-{Guid.NewGuid():N}");

    protected override AbsolutePath TemporaryFilesPrefix => Folder;

    [Fact(Timeout = 60000)]
    public async Task AStubbedGameAppliedOnceReportsHardlinkRecorded()
    {
        try
        {
            await LoadoutManager.ManageInstallation(GameInstallation);
            var loadout = await CreateLoadout();
            var path = new GamePath(LocationId.Game, "mods/a.txt");
            using (var tx = Connection.BeginTransaction())
            {
                var group = AddEmptyGroup(tx, loadout.LoadoutId, "Mod");
                AddFile(tx, loadout.LoadoutId, group, path, "content", out var hash, out var size);
                await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.FileName, new MemoryStream(Encoding.UTF8.GetBytes("content"))), hash, size)]);
                await tx.Commit();
            }
            await Synchronizer.Synchronize(loadout);
            var installations = new LoadoutManagedInstallations(
                Connection,
                GameRegistry,
                ServiceProvider.GetRequiredService<IDeploymentPlanStore>(),
                ServiceProvider.GetServices<IDeploymentBackend>(),
                ServiceProvider.GetRequiredService<IDeploymentBackendSelector>(),
                ServiceProvider.GetRequiredService<IGameLaunchSession>());

            var listed = installations.List().Should().ContainSingle().Subject;

            var game = listed.Deployment!.Single(r => r.Root == "Game");
            Assert.SkipWhen(game.Backend == DeploymentBackendId.Reflink.Value, "the temp directory supports FICLONE here");
            game.Backend.Should().Be("hardlink");
            game.Recorded.Should().BeTrue();
            game.Registered.Should().BeTrue();
            game.Chosen.Should().BeFalse();
            game.Reason.Should().Contain("EOPNOTSUPP");
            game.Available.Should().BeNull("an unchosen row already holds the chain's pick");
        }
        finally
        {
            if (Directory.Exists(Folder.ToString())) Directory.Delete(Folder.ToString(), recursive: true);
        }
    }
}
