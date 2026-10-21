using System.Runtime.CompilerServices;
using System.Text;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.Generic.Diagnostics;

/// <summary>
/// Reports a save or settings folder of a game that is a link to a folder that no longer exists, and offers to
/// move a backup folder back in its place.
/// </summary>
/// <remarks>
/// Looks at the game's mapped locations and at the folders it declares with <see cref="IGameUserDataFolders"/>,
/// and at the direct children of each. Links are never followed.
/// </remarks>
public class UserDataLinkEmitter : ILoadoutDiagnosticEmitter
{
    /// <inheritdoc />
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var diagnostic in DiagnoseInstallation(loadout.InstallationInstance))
            yield return diagnostic;
    }

    /// <summary>
    /// Diagnoses every folder <see cref="FoldersToCheck"/> returns for <paramref name="installation"/>.
    /// </summary>
    public static IEnumerable<Diagnostic> DiagnoseInstallation(GameInstallation installation)
        => DiagnoseFolders(installation.Game.DisplayName, FoldersToCheck(installation));

    /// <summary>
    /// The mapped locations other than the game folder, and the declared folders resolved through the file
    /// system of the installation, which for a Proton install maps the known paths into the prefix.
    /// </summary>
    public static IReadOnlyList<string> FoldersToCheck(GameInstallation installation)
    {
        var folders = new List<string>();

        // The game folder is the player's install, not user data, and is often huge.
        foreach (var (locationId, descriptor) in installation.Locations)
        {
            if (locationId == LocationId.Game) continue;
            folders.Add(descriptor.Path.GetFullPath());
        }

        var fileSystem = installation.LocatorResult.Path.FileSystem;
        foreach (var declared in GameUserDataFolders.For(installation.Game))
            folders.Add(fileSystem.GetKnownPath(declared.Root).Combine(declared.RelativePath).GetFullPath());

        return folders.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// One diagnostic per dangling link found at, or directly inside, any of <paramref name="folders"/>.
    /// </summary>
    public static IEnumerable<Diagnostic> DiagnoseFolders(string gameName, IEnumerable<string> folders)
    {
        foreach (var folder in folders)
        {
            foreach (var link in FindDanglingLinks(folder))
            {
                SavedFolderRestore.TryGetDanglingTarget(link, out var target);
                var backups = SavedFolderRestore.FindBackups(link);

                var diagnostic = Diagnostics.CreateDanglingUserDataLink(gameName, link, target, DescribeBackups(backups));

                // Several candidates and the app cannot tell which holds the saves, so it offers nothing.
                if (backups is [var only] && SavedFolderRestore.IsRestorableBackup(only))
                    diagnostic = diagnostic with { Actions = [DiagnosticActions.RestoreFolderFromBackup(link, only)] };

                yield return diagnostic;
            }
        }
    }

    private static IEnumerable<string> FindDanglingLinks(string folder)
    {
        if (SavedFolderRestore.IsDanglingLink(folder))
        {
            yield return folder;
            yield break;
        }

        // A link that resolves is somebody's deliberate choice, and a missing folder is nothing to report.
        if (!Directory.Exists(folder) || SavedFolderRestore.LinkTargetOf(folder) is not null) yield break;

        foreach (var child in Directory.EnumerateFileSystemEntries(folder))
        {
            if (SavedFolderRestore.IsDanglingLink(child)) yield return child;
        }
    }

    private static string DescribeBackups(IReadOnlyList<string> backups)
    {
        switch (backups.Count)
        {
            case 0:
                return "No backup folder was found next to it.";
            case 1:
                return SavedFolderRestore.IsRestorableBackup(backups[0])
                    ? $"A backup folder is next to it: `{backups[0]}`. Restoring it removes the broken link and moves the backup into its place."
                    : $"`{backups[0]}` looks like a backup but is empty or a link, so there is nothing to restore from it.";
        }

        var sb = new StringBuilder("Several folders next to it look like backups, so the app will not guess which one holds your files:\n\n");
        foreach (var backup in backups) sb.Append("* `").Append(backup).AppendLine("`");
        return sb.ToString();
    }
}
