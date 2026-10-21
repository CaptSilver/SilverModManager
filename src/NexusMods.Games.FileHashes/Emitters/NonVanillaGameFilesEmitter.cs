using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games.FileHashes;
using NexusMods.Abstractions.Games.FileHashes.Models;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FileHashes.Emitters;

/// <summary>
/// Reports the files in a game folder that are not the files the store installed. The list is the
/// non-vanilla rows of the installation's local baseline, so it only changes when the baseline is
/// rebuilt: it is the same after a restart and it goes away once a rebuild finds the files vanilla again.
/// Paths the loadout itself manages are left out, so what is listed is what nobody here put there.
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

        var files = NonVanillaFile.FindByBaseline(db, baseline.Value.Id)
            .Where(f => !IsManagedByLoadout(loadout.Db, syncTree, overridesGroup, f.Path))
            .OrderBy(f => f.Path.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0) yield break;

        var list = new StringBuilder();
        foreach (var file in files)
        {
            list.AppendLine($"* `{file.Path}` ({DescribeReason(file.Reason)})");
        }

        yield return Diagnostics.CreateNonVanillaGameFiles(
            Game: installation.Name,
            Store: installation.Store.Value,
            FileCount: files.Length,
            Files: list.ToString()
        );
    }

    /// <summary>
    /// Whether the loadout deploys or intends to deploy the file, or the game handles it itself. Three kinds
    /// of node do not count: ones that only carry a disk or previous state (no loadout hash), a file the
    /// loadout merely recorded from the game's initial state, and files in the overrides group, which is where
    /// the game folder's unmanaged files are filed when the game is first managed.
    /// </summary>
    private static bool IsManagedByLoadout(IDb loadoutDb, FrozenDictionary<GamePath, SyncNode> syncTree, EntityId overridesGroup, RelativePath path)
    {
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
