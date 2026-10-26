using System.Runtime.CompilerServices;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;

/// <summary>
/// A game INI the loadout owns: its baseline with the enabled tweaks merged in and the keys the app manages set on
/// top. Rendering only reads the database and the file store; <see cref="GameInis.Prepare"/> is what stores the
/// baseline and records what was baked, and <see cref="Ingest"/> is what folds the game's own edits into the baseline.
/// </summary>
public sealed class IniBakeIntrinsicFile(GamePath path, IGameIniFiles game, IFileStore fileStore) : IIntrinsicFileWithReset
{
    public GamePath Path { get; } = path;

    public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var bytes = AdoptedThisSync(syncTree, loadout) ?? await GameInis.Render(game, fileStore, loadout, Path);
        await stream.WriteAsync(bytes);
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
    /// in the file is not taken for the game's. The new baseline and the bytes it bakes to are stored here, and the
    /// bake is recorded in <paramref name="tx"/>. The apply's own loadout does not see that transaction, so the
    /// bytes are also kept for this sync's <see cref="Write"/>.
    /// </remarks>
    public async Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        if (syncTree.TryGetValue(Path, out var node) && !node.HaveDisk) return;
        if (GameInis.StateOf(loadout, Path) is not { } state) return;

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        var live = buffer.ToArray();
        if (live.Length == 0) return;

        Hash? deployed = node.HavePrevious ? node.Previous.Hash : null;
        if (await GameInis.LastWrittenBytes(fileStore, state, deployed) is not { } written) return;

        var adopted = (await GameInis.ChangesByGame(game, fileStore, loadout, Path, live, written))
            .Where(c => c.Kind == GameIniChangeKind.Adopted)
            .ToList();
        if (adopted.Count == 0) return;

        var baseline = IniDocument.Parse(await GameInis.LoadBaseline(fileStore, loadout, Path));
        foreach (var change in adopted) baseline.Set(change.Section, change.Key, change.Value);
        var baselineBytes = baseline.ToBytes();
        var baked = await GameInis.Render(game, fileStore, loadout, Path, baselineBytes);

        var pins = new List<IDisposable>();
        var baselineHash = await Store(baselineBytes, pins);
        var bakedHash = await Store(baked, pins);
        tx.Add(state.Id, LoadoutIniState.Baseline, baselineHash);
        tx.Add(state.Id, LoadoutIniState.LastBakedHash, bakedHash);

        Pending.GetOrCreateValue(syncTree)[Path] = new AdoptedBake(baselineHash, baked, pins);
    }

    private async Task<Hash> Store(byte[] bytes, List<IDisposable> pins)
    {
        var hash = bytes.xxHash3();
        pins.Add(FileStorePins.Pin(hash));
        if (!await fileStore.HaveFile(hash))
            await fileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(Path.FileName, new MemoryStream(bytes)), hash, Size.FromLong(bytes.Length))]);
        return hash;
    }

    // The loadout a sync hands to Write was read before Ingest's transaction commits, so it still holds the old
    // baseline. The bytes Ingest rendered stand in for it until the loadout catches up.
    private byte[]? AdoptedThisSync(Dictionary<GamePath, SyncNode> syncTree, Loadout.ReadOnly loadout)
    {
        if (!Pending.TryGetValue(syncTree, out var adopted) || !adopted.TryGetValue(Path, out var bake)) return null;
        return GameInis.StateOf(loadout, Path) is { } state && state.Baseline == bake.Baseline ? null : bake.Bytes;
    }

    private static readonly ConditionalWeakTable<Dictionary<GamePath, SyncNode>, Dictionary<GamePath, AdoptedBake>> Pending = new();

    /// <summary>A bake rendered from a baseline Ingest adopted. The store pins are released once the sync is done with it.</summary>
    private sealed class AdoptedBake(Hash baseline, byte[] bytes, List<IDisposable> pins)
    {
        public Hash Baseline { get; } = baseline;
        public byte[] Bytes { get; } = bytes;

        ~AdoptedBake()
        {
            foreach (var pin in pins) pin.Dispose();
        }
    }
}
