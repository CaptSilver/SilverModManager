using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Diagnostics;

/// <summary>
/// Reports sibling folders that differ only in letter case in a Windows game run through Wine or Proton, one card
/// per loadout listing every pair. Wine shows the game one folder of each pair, so the files in the other do not
/// load. It looks at what the loadout deploys and at what is on disk, vanilla and unmanaged files included, because
/// the game sees all of them. It never stops an Apply: a pair can come from the game's own files, which the app
/// cannot repair.
/// </summary>
public class CaseVariantFolderEmitter : ILoadoutDiagnosticEmitter
{
    /// <inheritdoc />
    [Obsolete("The sync tree overload is the one the diagnostic manager calls")]
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var synchronizer = loadout.InstallationInstance.GetGame().Synchronizer;
        var disk = synchronizer.GetLastScannedDiskState(loadout.Installation);
        var tree = synchronizer.BuildSyncTree(disk, disk, loadout);
        synchronizer.ProcessSyncTree(tree);
        if (Evaluate(loadout.InstallationInstance, tree) is { } diagnostic) yield return diagnostic;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (Evaluate(loadout.InstallationInstance, syncTree) is { } diagnostic) yield return diagnostic;
    }

    /// <summary>
    /// The card for the pairs in <paramref name="tree"/> and in the game folder's directories on disk, or null for a
    /// game that is not run through Wine or one without a pair. The directories are read because two spellings of a
    /// folder can hold a file of the same relative name, and the tree has one entry for those.
    /// </summary>
    internal static Diagnostic? Evaluate(GameInstallation installation, IReadOnlyDictionary<GamePath, SyncNode> tree)
    {
        if (!CaseVariantFolders.RunsUnderWine(installation)) return null;

        var pairs = CaseVariantFolders.FindAll(tree.Where(kv => kv.Value.HaveLoadout || kv.Value.HaveDisk).Select(kv => kv.Key), FoldersOnDisk(installation));
        if (pairs.Count == 0) return null;

        var list = new StringBuilder();
        foreach (var (first, second) in pairs) list.AppendLine($"* `{first}` and `{second}`");
        return CaseVariantFolderDiagnostics.CreateCaseVariantFolders(installation.Game.DisplayName, list.ToString().TrimEnd(), AdviceFor(installation.GetGame().Synchronizer.FoldersMergedOnApply));
    }

    private static string AdviceFor(IReadOnlyList<string> mergedOnApply)
    {
        const string byHand = "Move the files of one folder of the pair into the other, or delete them.";
        if (mergedOnApply.Count == 0) return byHand;

        var folders = string.Join(", ", mergedOnApply.Select(f => $"`{f}`"));
        return $"The next Apply merges a pair of {folders} folders. It leaves a file in place when the other folder already has a file of that name, and it does not touch other pairs. {byHand}";
    }

    private static IEnumerable<GamePath> FoldersOnDisk(GameInstallation installation)
    {
        var root = installation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty));
        if (!root.DirectoryExists()) return [];
        return root.EnumerateDirectories(recursive: true).Select(dir => new GamePath(LocationId.Game, dir.RelativeTo(root))).ToArray();
    }
}
