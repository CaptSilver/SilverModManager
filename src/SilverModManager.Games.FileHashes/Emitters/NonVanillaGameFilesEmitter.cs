using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes.Emitters;

/// <summary>
/// Reports the files in a game folder that are not the files the store installed. The list is the
/// non-vanilla rows of the installation's local baseline, so it only changes when the baseline is
/// rebuilt: it is the same after a restart and it goes away once a rebuild finds the files vanilla again.
/// Paths the loadout itself manages are left out, so what is listed is what nobody here put there. So are
/// the logs and crash dumps the game writes itself: they are counted in a closing line, not listed as mods.
/// </summary>
public class NonVanillaGameFilesEmitter : ILoadoutDiagnosticEmitter
{
    private readonly IFileHashesService _fileHashes;

    public NonVanillaGameFilesEmitter(IFileHashesService fileHashes)
    {
        _fileHashes = fileHashes;
    }

    // Without a sync tree nothing is known to be managed, so every non-vanilla row is listed. The
    // diagnostic manager always passes the tree.
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var installation = loadout.Installation;
        var db = _fileHashes.Local;

        var baseline = LocalBaseline.FindByGamePath(db, installation.Path)
            .Where(b => b.Store == installation.Store)
            .Cast<LocalBaseline.ReadOnly?>()
            .FirstOrDefault();
        if (baseline is null) yield break;

        var overridesGroup = LoadoutOverridesGroup.FindByOverridesFor(loadout.Db, loadout.Id).TryGetFirst(out var found)
            ? found.Id
            : default(EntityId);

        var gameInstalled = GameInstalledPaths.For(loadout.Db, loadout.LoadoutId);

        var isGameWritten = GameWrittenPredicate(loadout);
        var unmanaged = NonVanillaFile.FindByBaseline(db, baseline.Value.Id)
            .Where(f => !IsManagedByLoadout(loadout.Db, syncTree, overridesGroup, gameInstalled, f.Path))
            .ToArray();
        var files = unmanaged
            .Where(f => !isGameWritten(new GamePath(LocationId.Game, f.Path)))
            .OrderBy(f => f.Path.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0) yield break;
        var gameWritten = unmanaged.Length - files.Length;

        var list = new StringBuilder();
        foreach (var file in files)
        {
            list.AppendLine($"* `{file.Path}` ({DescribeReason(file.Reason)})");
        }
        if (gameWritten > 0)
        {
            list.AppendLine();
            list.AppendLine(gameWritten == 1
                ? "1 log or crash file the game wrote itself is not listed."
                : $"{gameWritten} log and crash files the game wrote itself are not listed.");
        }

        yield return Diagnostics.CreateNonVanillaGameFiles(
            Game: installation.Name,
            Store: installation.Store.Value,
            FileCount: files.Length,
            Files: list.ToString()
        );
    }

    // A game this build doesn't know (an old installation) has no synchronizer to ask, so the shared rule applies.
    private static Func<GamePath, bool> GameWrittenPredicate(Loadout.ReadOnly loadout)
        => loadout.TryGetGame(out var game) && game is IGame { Synchronizer: var synchronizer }
            ? synchronizer.IsGameWrittenPath
            : GameWrittenFiles.IsLogOrCrashFile;

    /// <summary>
    /// Whether the loadout deploys or intends to deploy the file, or the game handles it itself. A file the game
    /// installed itself (a Creation) is checked first: its node is Game-typed like a changed vanilla file, which
    /// must stay listed, so only the game-installed record can tell them apart. Three kinds
    /// of node do not count: ones that only carry a disk or previous state (no loadout hash), a file the
    /// loadout merely recorded from the game's initial state, and files in the overrides group, which is where
    /// the game folder's unmanaged files are filed when the game is first managed.
    /// </summary>
    private static bool IsManagedByLoadout(IDb loadoutDb, FrozenDictionary<GamePath, SyncNode> syncTree, EntityId overridesGroup, IReadOnlyDictionary<GamePath, (Hash Hash, Size Size)> gameInstalled, RelativePath path)
    {
        if (gameInstalled.ContainsKey(new GamePath(LocationId.Game, path))) return true;

        if (!syncTree.TryGetValue(new GamePath(LocationId.Game, path), out var node)) return false;

        return node.SourceItemType switch
        {
            LoadoutSourceItemType.Loadout => node.HaveLoadout && !IsInGroup(loadoutDb, node.Loadout.EntityId, overridesGroup),
            LoadoutSourceItemType.Intrinsic => true,
            _ => false,
        };
    }

    private static bool IsInGroup(IDb db, EntityId itemId, EntityId groupId)
    {
        var item = LoadoutItem.Load(db, itemId);
        return item.IsValid() && item.HasParent() && item.ParentId.Value == groupId;
    }

    private static string DescribeReason(NonVanillaReason reason) => reason switch
    {
        NonVanillaReason.NotInManifest => "not in the store's manifest",
        NonVanillaReason.SizeDiffers => "size differs",
        NonVanillaReason.HashDiffers => "content differs",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}
