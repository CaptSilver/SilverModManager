using SilverModManager.Sdk.Errors;
using SilverModManager.Abstractions.Diagnostics.Values;
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
/// cannot repair. An Apply merges the pairs it can, so the card is what is left: pairs the merge refused, folders the
/// app did not create, and any pair in a game that does not run under Wine.
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
        foreach (var (first, second) in pairs) list.AppendLine($"* {MarkdownText.Code(first)} and {MarkdownText.Code(second)}");
        return CaseVariantFolderDiagnostics.CreateCaseVariantFolders(installation.Game.DisplayName, new MarkdownFragment(list.ToString().TrimEnd()), AdviceFor(installation.GetGame().Synchronizer.MergesCaseVariantFoldersOnApply(installation)));
    }

    /// <summary>The advice under the list: what an Apply does about the pairs, or what the user has to do.</summary>
    internal static string AdviceFor(bool mergedOnApply)
        => mergedOnApply
            ? "The next Apply merges each pair into one folder. A file present in both spellings with different contents, the game's own folders, and saves stay where they are and are listed here; move or delete those by hand."
            : "Move the files of one folder of the pair into the other, or delete them.";

    private static IEnumerable<GamePath> FoldersOnDisk(GameInstallation installation)
    {
        var root = installation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, RelativePath.Empty));
        if (!root.DirectoryExists()) return [];
        return root.EnumerateDirectories(recursive: true).Select(dir => new GamePath(LocationId.Game, dir.RelativeTo(root))).ToArray();
    }
}
