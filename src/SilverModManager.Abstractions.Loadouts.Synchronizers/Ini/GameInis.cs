using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
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
/// Keeps a loadout's copy of the game's INI files: takes the first copy from disk, renders what each file should
/// contain, and records what was rendered so a later ingest can diff the file against it.
/// </summary>
public static class GameInis
{
    /// <summary>
    /// The files to write for <paramref name="loadout"/>: one per INI it has a baseline for. None when the loadout
    /// leaves the INIs to the game.
    /// </summary>
    public static IEnumerable<IIntrinsicFile> IntrinsicFiles(IGameIniFiles game, Loadout.ReadOnly loadout, IServiceProvider provider)
    {
        if (!loadout.IsValid() || !Loadout.OwnsGameInis.Get(loadout)) yield break;

        var fileStore = provider.GetRequiredService<IFileStore>();
        var owned = StatesByPath(loadout).Keys;
        foreach (var path in Paths(game).Where(owned.Contains))
            yield return new IniBakeIntrinsicFile(path, game, fileStore);
    }

    /// <summary>
    /// Brings the loadout's INI state up to date before a sync: a game INI on disk that the loadout has no baseline
    /// for is kept as its baseline (nothing is written to the file here), and each baseline's bake is stored and
    /// recorded as the last one written, so the bytes are in the store before they reach the game folder.
    /// </summary>
    /// <remarks>
    /// The bake is recorded here, not after the write, because writing an intrinsic file has no transaction. That is
    /// safe because rendering depends on the loadout's database state alone, so the write produces the same bytes.
    /// </remarks>
    public static async ValueTask<Loadout.ReadOnly> Prepare(IGameIniFiles game, Loadout.ReadOnly loadout, IServiceProvider provider)
    {
        if (!loadout.IsValid() || !Loadout.OwnsGameInis.Get(loadout)) return loadout;

        var connection = provider.GetRequiredService<IConnection>();
        var fileStore = provider.GetRequiredService<IFileStore>();

        loadout = await TakeBaselines(game, loadout, connection, fileStore);
        return await RecordBakes(game, loadout, connection, fileStore);
    }

    /// <summary>
    /// For a game's <c>AdjustActions</c>: a game INI the app deployed earlier that the loadout no longer provides, because
    /// it stopped owning the INIs, is left on disk and dropped from the disk state instead of deleted. The game needs
    /// its INI, and from then on it is the game's again.
    /// </summary>
    public static void LeaveDisownedInisOnDisk(IGameIniFiles game, Dictionary<GamePath, SyncNode> tree)
    {
        foreach (var path in Paths(game))
        {
            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(tree, path);
            if (Unsafe.IsNullRef(ref node)) continue;
            if (node.HaveLoadout || !node.HaveDisk || !node.HavePrevious || !node.Actions.HasFlag(Actions.DeleteFromDisk)) continue;

            node.Actions = Actions.ForgetDiskState;
        }
    }

    /// <summary>
    /// The bytes the game should find at <paramref name="path"/> for <paramref name="loadout"/>, built from
    /// <paramref name="baseline"/> when given and from the loadout's stored baseline otherwise.
    /// </summary>
    public static async Task<byte[]> Render(IGameIniFiles game, IFileStore fileStore, Loadout.ReadOnly loadout, GamePath path, byte[]? baseline = null)
    {
        var ini = IniDocument.Parse(baseline ?? await LoadBaseline(fileStore, loadout, path));

        foreach (var layer in IniTweakLayers.For(loadout, path))
            ini.Merge(IniDocument.Parse(await fileStore.Load(layer.Hash)));

        if (game.ManagedKeysFile == path)
            foreach (var key in game.ManagedKeys(loadout))
                ini.Set(key.Section, key.Key, key.Value);

        await game.RenderDerivedKeys(ini, path, loadout);
        return ini.ToBytes();
    }

    /// <summary>The baseline of <paramref name="path"/> as the file store holds it.</summary>
    /// <exception cref="IniBaselineMissingException">The loadout has no baseline for the path, or the store lost it.</exception>
    public static async Task<byte[]> LoadBaseline(IFileStore fileStore, Loadout.ReadOnly loadout, GamePath path)
    {
        if (!StatesByPath(loadout).TryGetValue(path, out var state) || !await fileStore.HaveFile(state.Baseline))
            throw new IniBaselineMissingException(path);
        return await fileStore.Load(state.Baseline);
    }

    /// <summary>The loadout's state row for <paramref name="path"/>, or null when it has no copy of that INI.</summary>
    public static LoadoutIniState.ReadOnly? StateOf(Loadout.ReadOnly loadout, GamePath path) =>
        StatesByPath(loadout).TryGetValue(path, out var state) ? state : null;

    /// <summary>
    /// What the app last wrote to <paramref name="path"/>, as the file store holds it: the bytes of
    /// <paramref name="deployed"/> when the store has them, which is what the disk state recorded for the last
    /// write, otherwise the bake recorded on the row. Null when the app has not written the file yet.
    /// </summary>
    /// <remarks>
    /// The deployed hash comes first because <see cref="Prepare"/> records the bake for the apply that is about to
    /// run before an ingest looks at it, so after a tweak was switched the recorded bake describes the next write,
    /// not the file the game changed.
    /// </remarks>
    public static async Task<byte[]?> LastWrittenBytes(IFileStore fileStore, LoadoutIniState.ReadOnly state, Hash? deployed)
    {
        if (deployed is { } hash && await fileStore.HaveFile(hash)) return await fileStore.Load(hash);
        return LoadoutIniState.LastBakedHash.TryGetValue(state, out var baked) && await fileStore.HaveFile(baked)
            ? await fileStore.Load(baked)
            : null;
    }

    /// <summary>
    /// The settings of the file the game left, <paramref name="live"/>, that differ from what the app last wrote,
    /// <paramref name="written"/>, sorted by what the next apply does with each. Settings the game removed are
    /// not changes: the next write puts them back.
    /// </summary>
    public static async Task<IReadOnlyList<GameIniChange>> ChangesByGame(
        IGameIniFiles game, IFileStore fileStore, Loadout.ReadOnly loadout, GamePath path, byte[] live, byte[] written)
    {
        var left = IniDocument.Parse(live);
        var last = IniDocument.Parse(written);

        var managed = game.ManagedKeysFile == path ? game.ManagedKeys(loadout) : [];
        var tweaks = new List<(string Name, IniDocument Document)>();
        foreach (var layer in IniTweakLayers.For(loadout, path))
            tweaks.Add((layer.Name, IniDocument.Parse(await fileStore.Load(layer.Hash))));

        var changes = new List<GameIniChange>();
        foreach (var section in left.Sections)
        {
            foreach (var (key, value) in left.Entries(section).DistinctBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (last.Get(section, key) == value) continue;

                if (managed.Any(m => Same(m.Section, section) && Same(m.Key, key)) || game.IsDerivedKey(path, section, key))
                    changes.Add(new GameIniChange(GameIniChangeKind.ManagedKeyReset, section, key, value));
                else if (tweaks.LastOrDefault(t => t.Document.Get(section, key) is not null) is { Name: not null } owner)
                    changes.Add(new GameIniChange(GameIniChangeKind.TweakOverrides, section, key, value, owner.Name));
                else
                    changes.Add(new GameIniChange(GameIniChangeKind.Adopted, section, key, value));
            }
        }

        return changes;
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The INI files of the game, as the paths the loadout stores them under.</summary>
    public static IReadOnlyList<GamePath> Paths(IGameIniFiles game) => game.IniFiles.FileNames.Select(game.IniPath).ToList();

    /// <summary>
    /// The INI a Bethesda game reads its settings from before the Prefs and Custom files: the first of
    /// <paramref name="files"/> that is neither a Prefs nor an editor (GECK) file, or null when there is none.
    /// </summary>
    public static GamePath? MainIni(IEnumerable<GamePath> files)
    {
        foreach (var path in files)
        {
            var name = path.FileName.ToString();
            if (!name.EndsWith("Prefs.ini", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("GECK", StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return null;
    }

    private static Dictionary<GamePath, LoadoutIniState.ReadOnly> StatesByPath(Loadout.ReadOnly loadout) =>
        LoadoutIniState.FindByLoadout(loadout.Db, loadout.LoadoutId).ToDictionary(state => (GamePath)state.Path);

    private static async Task<Loadout.ReadOnly> TakeBaselines(IGameIniFiles game, Loadout.ReadOnly loadout, IConnection connection, IFileStore fileStore)
    {
        var have = StatesByPath(loadout);
        var locations = loadout.InstallationInstance.Locations;

        var taken = new List<(GamePath Path, Hash Hash)>();
        var pins = new List<IDisposable>();
        try
        {
            foreach (var path in Paths(game).Where(p => !have.ContainsKey(p)))
            {
                if (!locations.ContainsKey(path.LocationId)) continue;

                var file = locations.ToAbsolutePath(path);
                if (!file.FileExists) continue;

                var bytes = await file.ReadAllBytesAsync();
                var hash = bytes.xxHash3();
                pins.Add(FileStorePins.Pin(hash));
                if (!await fileStore.HaveFile(hash))
                    await fileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.FileName, new MemoryStream(bytes)), hash, Size.FromLong(bytes.Length))]);
                taken.Add((path, hash));
            }

            if (taken.Count == 0) return loadout;

            using var tx = connection.BeginTransaction();
            foreach (var (path, hash) in taken)
            {
                _ = new LoadoutIniState.New(tx)
                {
                    LoadoutId = loadout.LoadoutId,
                    Path = path.ToGamePathParentTuple(loadout.LoadoutId),
                    Baseline = hash,
                };
            }
            await tx.Commit();
        }
        finally
        {
            foreach (var pin in pins) pin.Dispose();
        }

        return loadout.Rebase();
    }

    private static async Task<Loadout.ReadOnly> RecordBakes(IGameIniFiles game, Loadout.ReadOnly loadout, IConnection connection, IFileStore fileStore)
    {
        var recorded = new List<(EntityId State, Hash Hash)>();
        var pins = new List<IDisposable>();
        try
        {
            var states = StatesByPath(loadout);
            foreach (var path in Paths(game).Where(states.ContainsKey))
            {
                var state = states[path];
                var bytes = await Render(game, fileStore, loadout, path);
                var hash = bytes.xxHash3();
                if (LoadoutIniState.LastBakedHash.TryGetValue(state, out var last) && last == hash) continue;

                pins.Add(FileStorePins.Pin(hash));
                if (!await fileStore.HaveFile(hash))
                    await fileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.FileName, new MemoryStream(bytes)), hash, Size.FromLong(bytes.Length))]);
                recorded.Add((state.Id, hash));
            }

            if (recorded.Count == 0) return loadout;

            using var tx = connection.BeginTransaction();
            foreach (var (state, hash) in recorded) tx.Add(state, LoadoutIniState.LastBakedHash, hash);
            await tx.Commit();
        }
        finally
        {
            foreach (var pin in pins) pin.Dispose();
        }

        return loadout.Rebase();
    }
}
