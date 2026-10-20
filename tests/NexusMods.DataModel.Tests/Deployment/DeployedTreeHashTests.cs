using System.Text;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;

namespace NexusMods.DataModel.Tests.Deployment;

/// <summary>
/// Pins the bytes and executable bits that land in the game and preferences folders through
/// apply, disable and reset. The verified file must not change when the way files reach
/// disk is reworked.
/// </summary>
public class DeployedTreeHashTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<DeployedTreeHashTests>(helper)
{
    protected override async Task GenerateGameFiles()
    {
        var originalFile = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "bin/originalGameFile.txt"));
        originalFile.Parent.CreateDirectory();
        await originalFile.WriteAllTextAsync("Hello World!");

        var settings = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Preferences, "settings.ini"));
        settings.Parent.CreateDirectory();
        await settings.WriteAllTextAsync("[settings]\nvolume=7\n");
    }

    [Fact]
    public async Task DeployedTreeIsStable()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        var loadout = await CreateLoadout();

        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "Deployed tree mod");
        var modFiles = new[]
        {
            ("Data/mod.pak", "mod pak bytes"),
            ("bin/launcher", "#!/bin/sh\necho launcher\n"),
            ("README.txt", "README overridden by the mod"),
        };
        var archived = new List<ArchivedFileEntry>();
        foreach (var (path, content) in modFiles)
        {
            AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, path), content, out var hash, out var size);
            archived.Add(new ArchivedFileEntry(new MemoryStreamFactory(path, new MemoryStream(Encoding.UTF8.GetBytes(content))), hash, size));
        }
        await FileStore.BackupFiles(archived);
        var result = await tx.Commit();
        var committedGroup = result[group.Value];

        loadout = await Synchronizer.Synchronize(loadout);

        var gameRoot = GameInstallation.Locations[LocationId.Game].Path;
        var preferencesRoot = GameInstallation.Locations[LocationId.Preferences].Path;
        var sb = new StringBuilder();

        await Section(sb, "## 1 applied", gameRoot, preferencesRoot);

        await DisableItem(committedGroup);
        await Synchronizer.Synchronize(loadout);
        await Section(sb, "## 2 mod disabled", gameRoot, preferencesRoot);

        await Synchronizer.ResetToOriginalGameState(GameInstallation, GameInstallation.LocatorResult.LocatorIds.Distinct().ToArray());
        await Section(sb, "## 3 reset", gameRoot, preferencesRoot);

        await Verify(sb.ToString(), extension: "txt");
    }

    private static async Task Section(StringBuilder sb, string title, AbsolutePath game, AbsolutePath preferences)
    {
        sb.AppendLine(title);
        sb.AppendLine("### Game");
        sb.Append(await DirectoryDescription.Describe(game));
        sb.AppendLine("### Preferences");
        sb.Append(await DirectoryDescription.Describe(preferences));
        sb.AppendLine();
    }
}
