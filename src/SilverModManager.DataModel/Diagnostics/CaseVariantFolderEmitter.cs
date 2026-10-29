using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
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
    /// The card for the pairs in <paramref name="tree"/>, or null for a game that is not run through Wine or a tree
    /// without a pair.
    /// </summary>
    internal static Diagnostic? Evaluate(GameInstallation installation, IReadOnlyDictionary<GamePath, SyncNode> tree)
    {
        if (!CaseVariantFolders.RunsUnderWine(installation)) return null;

        var pairs = CaseVariantFolders.FindAll(tree.Where(kv => kv.Value.HaveLoadout || kv.Value.HaveDisk).Select(kv => kv.Key));
        if (pairs.Count == 0) return null;

        var list = new StringBuilder();
        foreach (var (first, second) in pairs) list.AppendLine($"* `{first}` and `{second}`");
        return CaseVariantFolderDiagnostics.CreateCaseVariantFolders(installation.Game.DisplayName, list.ToString().TrimEnd());
    }
}
