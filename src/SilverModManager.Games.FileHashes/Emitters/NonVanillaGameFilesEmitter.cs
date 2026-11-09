using SilverModManager.Sdk.Errors;
using SilverModManager.Abstractions.Diagnostics.Values;
using SilverModManager.Sdk.Hashes;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
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
    private readonly IGameRegistry _registry;
    private readonly ILogger? _logger;

    public NonVanillaGameFilesEmitter(IFileHashesService fileHashes, IGameRegistry registry, ILogger<NonVanillaGameFilesEmitter>? logger = null)
    {
        _fileHashes = fileHashes;
        _registry = registry;
        _logger = logger;
    }

    // Without a sync tree nothing is known about the folder, so every non-vanilla row is listed. The
    // diagnostic manager always passes the tree.
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => DiagnoseCore(loadout, null, cancellationToken);

    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, CancellationToken cancellationToken)
        => DiagnoseCore(loadout, syncTree, cancellationToken);

    private async IAsyncEnumerable<Diagnostic> DiagnoseCore(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode>? syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var installation = loadout.Installation;
        var db = _fileHashes.Local;

        var baseline = FindBaseline(db, loadout);
        if (baseline is null) yield break;

        var overridesGroup = OverridesGroupLookup.Find(loadout);

        var gameInstalled = GameInstalledPaths.For(loadout.Db, loadout.LoadoutId);

        var isGameWritten = GameWrittenPredicate(loadout);
        var isFiltered = FilteredPredicate(loadout);
        var unmanaged = NonVanillaFile.FindByBaseline(db, baseline.Value.Id)
            // A baseline built before the synchronizer filtered a path still holds its row; a new baseline would not.
            .Where(f => !isFiltered(new GamePath(LocationId.Game, f.Path)))
            // The rows are as old as the baseline: a path the sync tree has no node for is neither on disk nor in the
            // loadout any more, and the card describes the folder as it is now.
            .Where(f => syncTree is null || HasNode(syncTree, f.Path))
            .Where(f => !IsManagedByLoadout(loadout.Db, syncTree ?? FrozenDictionary<GamePath, SyncNode>.Empty, overridesGroup, gameInstalled, f.Path, _logger))
            .ToArray();
        var files = unmanaged
            .Where(f => !isGameWritten(new GamePath(LocationId.Game, f.Path)))
            .OrderBy(f => f.Path.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0) yield break;
        var gameWritten = unmanaged.Length - files.Length;

        var folders = DescribeFolders(files.Select(f => f.Path));
        var list = new StringBuilder(DescribeFiles(files));
        if (gameWritten > 0)
        {
            list.AppendLine();
            list.AppendLine(gameWritten == 1
                ? "1 log or crash file the game wrote itself is not listed."
                : $"{gameWritten} log and crash files the game wrote itself are not listed.");
        }

        yield return Diagnostics.CreateNonVanillaGameFiles(
            Game: installation.Name,
            TheGame: Diagnostics.WithArticle(installation.Name),
            Store: installation.Store.Value,
            FileCount: files.Length,
            Folders: new MarkdownFragment(folders),
            Files: new MarkdownFragment(list.ToString())
        );
    }

    /// <summary>
    /// The baseline is keyed by the folder the game registry located, which can be spelled differently from the
    /// installation row (on an ostree host /home is a link to /var/home), so it is looked up the way it was written. A game
    /// the registry cannot find falls back to the row's folder, the one it was managed in.
    /// </summary>
    private LocalBaseline.ReadOnly? FindBaseline(IDb db, Loadout.ReadOnly loadout)
    {
        // The loadout's ids, not the located install's: the notice describes what this loadout's sync sees, and
        // a baseline built for other ids is one the sync ignores.
        var ids = loadout.LocatorIds.Select(id => id.Value);
        if (_registry.TryGetGameInstallation(loadout, out var located))
            return LocalBaselineCoordinator.FindBaseline(db, located.LocatorResult.Store, LocalBaselineCoordinator.GamePathOf(located), ids);

        var row = loadout.Installation;
        return LocalBaselineCoordinator.FindBaseline(db, row.Store, row.Path, ids);
    }

    // A game this build doesn't know (an old installation) has no synchronizer to ask, so the shared rule applies.
    private static Func<GamePath, bool> GameWrittenPredicate(Loadout.ReadOnly loadout)
        => loadout.TryGetGame(out var game) && game is IGame { Synchronizer: var synchronizer }
            ? synchronizer.IsGameWrittenPath
            : GameWrittenFiles.IsLogOrCrashFile;

    // Files no game loads are never listed, whichever synchronizer the game has: an older baseline still holds their rows.
    private static Func<GamePath, bool> FilteredPredicate(Loadout.ReadOnly loadout)
        => loadout.TryGetGame(out var game) && game is IGame { Synchronizer: var synchronizer }
            ? path => NeverLoadedFiles.IsNeverLoaded(path) || synchronizer.GamePathFilter.ShouldFilter(path)
            : NeverLoadedFiles.IsNeverLoaded;

    /// <summary>
    /// Whether the loadout deploys or intends to deploy the file, an earlier apply deployed it, or the game handles
    /// it itself. A file the game installed itself (a Creation) is checked first: its node is Game-typed like a
    /// changed vanilla file, which must stay listed, so only the game-installed record can tell them apart. Files in
    /// the overrides group do not count, whatever the previous state says: that is where the game folder's
    /// unmanaged files are filed when the game is first managed, so they are the user's own. Three kinds of node
    /// do not count either: ones that only carry a disk state, a file the loadout merely recorded from the game's
    /// initial state, and a loadout node with no hash. A non-game node with a previous state does count even when the loadout
    /// no longer lists the path (a disabled mod, a merged file no merge plans any more): this app wrote the file and
    /// the next apply removes it, so it was not there beforehand.
    /// </summary>
    private static bool IsManagedByLoadout(IDb loadoutDb, FrozenDictionary<GamePath, SyncNode> syncTree, EntityId overridesGroup, IReadOnlyDictionary<GamePath, (FileIdentity Identity, Size Size)> gameInstalled, RelativePath path, ILogger? logger)
    {
        var gamePath = new GamePath(LocationId.Game, path);
        if (gameInstalled.ContainsKey(gamePath)) return true;

        if (!syncTree.TryGetValue(gamePath, out var node))
        {
            logger?.LogTrace("`{Path}` is not managed: the sync tree has no node for it", path);
            return false;
        }

        if (node.SourceItemType == LoadoutSourceItemType.Intrinsic) return true;

        if (node.SourceItemType == LoadoutSourceItemType.Loadout && node.HaveLoadout && OverridesGroupLookup.IsInGroup(loadoutDb, node.Loadout.EntityId, overridesGroup))
        {
            logger?.LogTrace("`{Path}` is not managed: the loadout holds it in its overrides group", path);
            return false;
        }

        // A game file changed since it was tracked is the very thing this notice is for, whatever the previous state says.
        if (node.HavePrevious && node.SourceItemType != LoadoutSourceItemType.Game) return true;

        var managed = node.SourceItemType == LoadoutSourceItemType.Loadout && node.HaveLoadout;
        if (!managed) logger?.LogTrace("`{Path}` is not managed: source {Source}, loadout identity {HaveLoadout}, previous state {HavePrevious}", path, node.SourceItemType, node.HaveLoadout, node.HavePrevious);
        return managed;
    }

    private bool HasNode(FrozenDictionary<GamePath, SyncNode> syncTree, RelativePath path)
    {
        if (syncTree.ContainsKey(new GamePath(LocationId.Game, path))) return true;
        _logger?.LogTrace("`{Path}` is not listed: the sync tree has no node for it, so it is not in the folder", path);
        return false;
    }

    // A folder with only a few files lists each of them with its reason. A larger one is a single line with its count: a
    // pack unpacked by hand can be hundreds of files, and one line each would bury the rest of the card.
    private const int MostFilesListedOneByOne = 5;

    // Files are counted per folder two levels down (Data/textures), not per leaf folder. A mod spread over many leaf folders
    // of a few files each would otherwise still be one line per file.
    private const int FolderLevelsShown = 2;

    private static string DescribeFiles(IEnumerable<NonVanillaFile.ReadOnly> files)
    {
        var lines = new List<(bool IsTop, string SortKey, string Line)>();
        foreach (var folder in files.GroupBy(f => FolderKey(f.Path)))
        {
            var inFolder = folder.ToArray();
            if (inFolder.Length <= MostFilesListedOneByOne)
            {
                lines.AddRange(inFolder.Select(file => (false, file.Path.ToString(), $"* {MarkdownText.Code(file.Path)} ({DescribeReason(file.Reason)})")));
                continue;
            }

            var isTop = folder.Key.Length == 0;
            var where = isTop ? "the top of the game folder" : MarkdownText.Code(folder.Key + "/");
            lines.Add((isTop, folder.Key + "/", $"* {where}: {inFolder.Length} files ({DescribeReasons(inFolder.Select(f => f.Reason))})"));
        }

        // The files at the top of the game folder come last, as they do in the summary by top-level folder.
        var text = new StringBuilder();
        foreach (var (_, _, line) in lines.OrderBy(l => l.IsTop).ThenBy(l => l.SortKey, StringComparer.Ordinal)) text.AppendLine(line);
        return text.ToString();
    }

    private static string FolderKey(RelativePath path)
        => path.Depth == 0 ? "" : string.Join('/', path.Parent.GetParts().Take(FolderLevelsShown).Select(part => part.ToString()));

    // The one reason when they all agree, otherwise each with its count, the most common first.
    private static string DescribeReasons(IEnumerable<NonVanillaReason> reasons)
    {
        var counts = reasons.GroupBy(r => r).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToArray();
        return counts.Length == 1
            ? DescribeReason(counts[0].Key)
            : string.Join(", ", counts.Select(g => DescribeReasonCount(g.Key, g.Count())));
    }

    // One line per top-level folder, sorted by name, with the files at the top of the game folder last.
    private static string DescribeFolders(IEnumerable<RelativePath> paths)
    {
        var groups = paths
            .GroupBy(p => p.Depth > 0 ? p.GetParts().First().ToString() : null)
            .OrderBy(g => g.Key is null ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.Ordinal);

        var text = new StringBuilder();
        foreach (var group in groups)
        {
            var count = group.Count();
            var noun = count == 1 ? "file" : "files";
            text.AppendLine(group.Key is null
                ? $"* the top of the game folder: {count} {noun}"
                : $"* {MarkdownText.Code(group.Key + "/")}: {count} {noun}");
        }
        return text.ToString();
    }

    // The reason as it reads after a count: "4 not in the store's manifest", "3 with different content".
    private static string DescribeReasonCount(NonVanillaReason reason, int count) => reason switch
    {
        NonVanillaReason.NotInManifest => $"{count} not in the store's manifest",
        NonVanillaReason.SizeDiffers => $"{count} with a different size",
        NonVanillaReason.HashDiffers => $"{count} with different content",
        NonVanillaReason.LooksLikeAMod => $"{count} look like a mod loader or mod",
        NonVanillaReason.DeployedByLoadout => $"{count} put there by a loadout, which no longer provides them",
        NonVanillaReason.DeployedByAnotherManager => $"{count} put there by another mod manager",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };

    private static string DescribeReason(NonVanillaReason reason) => reason switch
    {
        NonVanillaReason.NotInManifest => "not in the store's manifest",
        NonVanillaReason.SizeDiffers => "size differs",
        NonVanillaReason.HashDiffers => "content differs",
        NonVanillaReason.LooksLikeAMod => "looks like a mod loader or mod; no store file list was available to check",
        NonVanillaReason.DeployedByLoadout => "put there by a loadout, which no longer provides it",
        NonVanillaReason.DeployedByAnotherManager => "put there by another mod manager",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null),
    };
}
