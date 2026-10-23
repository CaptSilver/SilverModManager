using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Games.TestFramework;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests.Deployment;

/// <summary>
/// A stubbed game with one mod that puts a few files into the game folder, ready to be applied.
/// </summary>
public abstract class AppliedGameTest<TTest>(ITestOutputHelper helper) : AStubbedGameIsolatedTest<TTest>(helper)
{
    protected const int TimeoutMs = 60000;

    protected static readonly GamePath GameRoot = new(LocationId.Game, RelativePath.Empty);
    protected static readonly GamePath ModFile = new(LocationId.Game, "mods/a.txt");
    protected static readonly GamePath SecondModFile = new(LocationId.Game, "mods/b.txt");

    protected IDeploymentPlanStore Plans => ServiceProvider.GetRequiredService<IDeploymentPlanStore>();

    protected GameInstallMetadataId MetadataId => GameRegistry.ForceGetMetadata(GameInstallation).Id;

    protected DeploymentPlan Plan => Plans.Load(Connection.Db, MetadataId);

    protected AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    protected Task<string> DescribeGameFolder() => DirectoryDescription.Describe(GameInstallation.Locations[LocationId.Game].Path);

    /// <summary>
    /// Manages the installation and creates a loadout whose mod holds <see cref="ModFile"/> and
    /// <see cref="SecondModFile"/>. Nothing is applied.
    /// </summary>
    protected async Task<Loadout.ReadOnly> NewLoadoutWithMod()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();

        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "Mod");
        var archived = new List<ArchivedFileEntry>();
        foreach (var path in new[] { ModFile, SecondModFile })
        {
            var content = $"content of {path}";
            AddFile(tx, loadout.LoadoutId, group, path, content, out var hash, out var size);
            archived.Add(new ArchivedFileEntry(new MemoryStreamFactory(path.FileName, new MemoryStream(Encoding.UTF8.GetBytes(content))), hash, size));
        }
        await FileStore.BackupFiles(archived);
        await tx.Commit();
        return loadout;
    }
}
