using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.CreationEngine.Abstractions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.Emitters;

/// <summary>
/// Warns about a DLL in the script extender's plugin folder (Data/SKSE/Plugins and its F4SE and SFSE equivalents)
/// that is on disk but that no enabled mod provides. The game loads every DLL there, so a leftover can crash it.
/// </summary>
/// <remarks>
/// A file in the loadout's Overrides group counts as not provided: the group holds what was found in the game
/// folder, not what the user installed through a mod. Files the game or the store put there, and files an earlier
/// apply deployed that the next apply removes, are not reported.
/// </remarks>
public class UnmanagedScriptExtenderPluginEmitter(ICreationEngineGame game) : ASyncTreeEmitter
{
    /// <inheritdoc />
    protected override async IAsyncEnumerable<Diagnostic> DiagnoseTree(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (game.ScriptExtenderPluginFolder is not { } folder) yield break;

        var provided = ModProvidedFiles.For(loadout);
        var gameInstalled = GameInstalledPaths.For(loadout.Db, loadout.LoadoutId);

        var leftovers = syncTree
            .Where(pair => NativeCodePaths.IsLibraryBelow(pair.Key, folder))
            .Where(pair => IsUnmanaged(pair.Key, pair.Value, provided, gameInstalled))
            .OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal);

        foreach (var (path, node) in leftovers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = Diagnostics.CreateUnmanagedScriptExtenderPlugin(game.DisplayName, path.Path.ToString(), folder.Path.ToString());
            yield return OverridesGroupOf(loadout.Db, node) is { } group
                ? diagnostic with { Actions = [DiagnosticActions.RemoveLoadoutFile(path, group)] }
                : diagnostic;
        }
    }

    // The row the node's loadout side points at, which is an Overrides row for a node IsUnmanaged lets through; a
    // disk-only file has none and so nothing to remove. The action is scoped to that group so a mod placing the
    // same file keeps its row.
    private static EntityId? OverridesGroupOf(IDb db, SyncNode node)
    {
        if (!node.HaveLoadout || !node.Loadout.HaveEntityId) return null;
        var item = LoadoutItem.Load(db, node.Loadout.EntityId);
        return item.IsValid() && item.HasParent() ? item.ParentId : null;
    }

    private static bool IsUnmanaged(GamePath path, SyncNode node, ModProvidedFiles provided, IReadOnlyDictionary<GamePath, (FileIdentity Identity, Size Size)> gameInstalled)
    {
        if (!node.HaveDisk) return false;
        if (gameInstalled.ContainsKey(path)) return false;
        if (node.SourceItemType is LoadoutSourceItemType.Game or LoadoutSourceItemType.Intrinsic) return false;
        if (provided.IsProvidedByMod(node)) return false;

        // The app wrote it in an earlier apply and no enabled mod has it now, so the next apply takes it off disk.
        if (node.HavePrevious && !node.HaveLoadout) return false;

        // A row of the Overrides group is a leftover; a disk-only file has no loadout side at all.
        return true;
    }
}
