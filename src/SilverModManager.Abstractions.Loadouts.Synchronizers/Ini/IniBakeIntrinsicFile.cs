using System.Runtime.CompilerServices;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;

/// <summary>
/// A game INI the loadout owns: its baseline with the enabled tweaks merged in and the keys the app manages set on
/// top. Rendering only reads the database and the file store; <see cref="GameInis.Prepare"/> stores the baseline,
/// <see cref="RecordWrite"/> records what the sync wrote, and <see cref="Ingest"/> is what folds the game's own edits
/// into the baseline.
/// </summary>
public sealed class IniBakeIntrinsicFile(GamePath path, IGameIniFiles game, IFileStore fileStore) : IIntrinsicFileWithReset, IIntrinsicFileWithWriteRecord
{
    public GamePath Path { get; } = path;

    public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var bytes = AdoptedThisSync(syncTree, loadout) ?? await GameInis.Render(game, fileStore, loadout, Path);
        await stream.WriteAsync(bytes);
    }

    /// <summary>
    /// Records the bytes the sync is about to write as the last bake, in the apply's transaction, so the hash lands
    /// only for a write that landed.
    /// </summary>
    public async Task RecordWrite(IntrinsicWrite write)
    {
        // The file exists because GameInis.IntrinsicFiles found this row, so a missing one is a bug, not a case.
        var state = GameInis.StateOf(write.Loadout, Path)
                    ?? throw new InvalidOperationException($"{Path} is written for loadout {write.Loadout.LoadoutId}, which has no INI state row for it");
        // An intrinsic node is written whenever the loadout provides the file, changed or not, so most writes repeat
        // the recorded bake; those add no datom.
        if (LoadoutIniState.LastBakedHash.TryGetValue(state, out var last) && last == write.Hash) return;

        TransactionPins.Keep(write.Transaction, await IniEdits.Store(fileStore, Path, write.Bytes));
        write.Transaction.Add(state.Id, LoadoutIniState.LastBakedHash, write.Hash);
    }

    /// <summary>
    /// The player's own copy, as it was before the loadout took the file over. The tweaks and managed keys belong to
    /// the loadout, so a reset leaves them out.
    /// </summary>
    public async Task<byte[]?> RenderForReset(Loadout.ReadOnly lastSyncedLoadout, IReadOnlyDictionary<GamePath, SyncNode> desiredState) =>
        await GameInis.LoadBaseline(fileStore, lastSyncedLoadout, Path);

    /// <summary>
    /// Folds what the game changed in the file since the last apply into the loadout's baseline, so the write that
    /// follows keeps it: a setting that neither the app nor an enabled mod's tweak owns is the player's choice made
    /// in the game's own menu. A key the app manages or a tweak owns is not adopted; the write puts its value back
    /// (<see cref="GameInis.ChangesByGame"/> lists them for the diagnostics).
    /// </summary>
    /// <remarks>
    /// The file is compared with the bytes the app last wrote, not with the baseline, so a setting a tweak put
    /// in the file is not taken for the game's. The new baseline is stored and recorded here, in <paramref name="tx"/>;
    /// the bake is recorded by the sync's write through <see cref="RecordWrite"/>. The apply's own loadout does not
    /// see that transaction, so the bytes the baseline bakes to are kept for this sync's <see cref="Write"/>.
    /// </remarks>
    public async Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        if (syncTree.TryGetValue(Path, out var node) && !node.HaveDisk) return;
        if (GameInis.StateOf(loadout, Path) is not { } state) return;

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        var live = buffer.ToArray();
        if (live.Length == 0) return;

        // A previous part of another kind is a vanilla file the app never baked an ini over.
        FileIdentity? deployed = node.HavePrevious && node.Previous.Identity.Kind == HashKind.XxHash3 ? node.Previous.Identity : null;
        if (await GameInis.LastWrittenBytes(fileStore, state, deployed) is not { } written) return;

        var adopted = (await GameInis.ChangesByGame(game, fileStore, loadout, Path, live, written))
            .Where(c => c.Kind == GameIniChangeKind.Adopted)
            .ToList();
        if (adopted.Count == 0) return;

        var baseline = IniDocument.Parse(await GameInis.LoadBaseline(fileStore, loadout, Path));
        foreach (var change in adopted) baseline.Set(change.Section, change.Key, change.Value);
        var baselineBytes = baseline.ToBytes();
        var baked = await GameInis.Render(game, fileStore, loadout, Path, baselineBytes);

        var baselineHash = baselineBytes.xxHash3();
        TransactionPins.Keep(tx, await IniEdits.Store(fileStore, Path, baselineBytes));
        tx.Add(state.Id, LoadoutIniState.Baseline, baselineHash);

        Pending.GetOrCreateValue(syncTree)[Path] = new AdoptedBake(baselineHash, baked);
    }

    // The loadout a sync hands to Write was read before Ingest's transaction commits, so it still holds the old
    // baseline. The bytes Ingest rendered stand in for it until the loadout catches up.
    private byte[]? AdoptedThisSync(Dictionary<GamePath, SyncNode> syncTree, Loadout.ReadOnly loadout)
    {
        if (!Pending.TryGetValue(syncTree, out var adopted) || !adopted.TryGetValue(Path, out var bake)) return null;
        return GameInis.StateOf(loadout, Path) is { } state && state.Baseline == bake.Baseline ? null : bake.Bytes;
    }

    private static readonly ConditionalWeakTable<Dictionary<GamePath, SyncNode>, Dictionary<GamePath, AdoptedBake>> Pending = new();

    /// <summary>A bake rendered from a baseline Ingest adopted; the sync's write records it through <see cref="RecordWrite"/>.</summary>
    private sealed record AdoptedBake(Hash Baseline, byte[] Bytes);
}
