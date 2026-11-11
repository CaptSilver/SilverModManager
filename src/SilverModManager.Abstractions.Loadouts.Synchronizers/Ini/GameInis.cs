using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
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
    /// for is kept as its baseline. Nothing is written to the file here and no bake is recorded; the sync's write
    /// records that through <see cref="IniBakeIntrinsicFile.RecordWrite"/>.
    /// </summary>
    public static async ValueTask<Loadout.ReadOnly> Prepare(IGameIniFiles game, Loadout.ReadOnly loadout, IServiceProvider provider)
    {
        if (!loadout.IsValid() || !Loadout.OwnsGameInis.Get(loadout)) return loadout;

        var connection = provider.GetRequiredService<IConnection>();
        var fileStore = provider.GetRequiredService<IFileStore>();

        return await TakeBaselines(game, loadout, connection, fileStore);
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
    /// For a game's <c>AdjustActions</c>: the loadout's files in the INI tweak folder stay in the loadout but are
    /// never written, and a copy found on disk is deleted, backed up first when the file store lacks its bytes.
    /// </summary>
    /// <remarks>
    /// Leaving an unexpected copy on disk untracked does not keep it: the next index records it again, and the
    /// apply after that would delete it without a backup. A tweak file the loadout no longer has keeps the generic
    /// actions, which already remove a file that stops being deployed.
    /// </remarks>
    public static void KeepTweaksOffDisk(Dictionary<GamePath, SyncNode> tree)
    {
        foreach (var path in tree.Keys.Where(IniTweakLayers.IsTweakPath).ToArray())
        {
            ref var node = ref CollectionsMarshal.GetValueRefOrNullRef(tree, path);
            if (node.SourceItemType is LoadoutSourceItemType.Intrinsic || !node.HaveLoadout) continue;

            node.Actions = !node.HaveDisk
                ? Actions.DoNothing
                : node.Signature.HasFlag(Signature.DiskArchived)
                    ? Actions.DeleteFromDisk
                    : Actions.BackupFile | Actions.DeleteFromDisk;
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
            ini.Merge(await LoadLayer(fileStore, layer, path));

        if (game.ManagedKeysFile == path)
            foreach (var key in game.ManagedKeys(loadout))
                ini.Set(key.Section, key.Key, key.Value);

        await game.RenderDerivedKeys(ini, path, loadout);
        return ini.ToBytes();
    }

    private static async Task<IniDocument> LoadLayer(IFileStore fileStore, IniTweakLayer layer, GamePath path) =>
        await fileStore.HaveFile(layer.Identity)
            ? IniDocument.Parse(await fileStore.Load(layer.Identity))
            : throw IniEdits.GoneLayer(layer.Name, layer.SourceName, path);

    /// <summary>The baseline of <paramref name="path"/> as the file store holds it.</summary>
    /// <exception cref="IniBaselineMissingException">The loadout has no baseline for the path, or the store lost it.</exception>
    public static async Task<byte[]> LoadBaseline(IFileStore fileStore, Loadout.ReadOnly loadout, GamePath path)
    {
        if (!StatesByPath(loadout).TryGetValue(path, out var state) || !await fileStore.HaveFile(state.BaselineIdentity))
            throw new IniBaselineMissingException(path);
        return await fileStore.Load(state.BaselineIdentity);
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
    /// The deployed hash comes first because it is what this game folder's last write actually left, while the
    /// row's bake can describe a write made for another loadout (<c>CopyLoadout</c> carries it over). The bake is
    /// the fallback when the previous part is of another identity kind, a vanilla file the app never baked over,
    /// or the store lacks the deployed bytes.
    /// </remarks>
    public static async Task<byte[]?> LastWrittenBytes(IFileStore fileStore, LoadoutIniState.ReadOnly state, FileIdentity? deployed)
    {
        if (deployed is { } deployedIdentity && await fileStore.HaveFile(deployedIdentity))
            return await fileStore.Load(deployedIdentity);
        return state.LastBakedIdentity() is { } baked && await fileStore.HaveFile(baked)
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

        var tweaks = new List<(string Name, IniDocument Document)>();
        foreach (var layer in IniTweakLayers.For(loadout, path))
            tweaks.Add((OwnerOf(layer), await LoadLayer(fileStore, layer, path)));

        var changes = new List<GameIniChange>();
        foreach (var section in left.Sections)
        {
            foreach (var (key, value) in left.Entries(section).DistinctBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (last.Get(section, key) == value) continue;

                if (AppSetsKey(game, loadout, path, section, key) is not null)
                    changes.Add(new GameIniChange(GameIniChangeKind.ManagedKeyReset, section, key, value));
                else if (tweaks.LastOrDefault(t => t.Document.Get(section, key) is not null) is { Name: not null } owner)
                    changes.Add(new GameIniChange(GameIniChangeKind.TweakOverrides, section, key, value, owner.Name));
                else
                    changes.Add(new GameIniChange(GameIniChangeKind.Adopted, section, key, value));
            }
        }

        return changes;
    }

    // "Loadout (Yggdrasil VR)" for a modlist's Loadout-named file, so it is not mistaken for the user's own tweak.
    private static string OwnerOf(IniTweakLayer layer) =>
        layer.Name.Equals(layer.SourceName, StringComparison.Ordinal) ? layer.Name : $"{layer.Name} ({layer.SourceName})";

    /// <summary>
    /// What the app writes to the key on every Apply, or null when the key is not one the app sets itself: the managed
    /// value (with " on every Apply") or "rendered from the loadout" for a derived key.
    /// </summary>
    public static string? AppSetsKey(IGameIniFiles game, Loadout.ReadOnly loadout, GamePath path, string section, string key)
    {
        if (game.ManagedKeysFile == path
            && game.ManagedKeys(loadout).FirstOrDefault(m => Same(m.Section, section) && Same(m.Key, key)) is { Key: not null } managed)
            return $"{managed.Value} on every Apply";
        return game.IsDerivedKey(path, section, key) ? "rendered from the loadout" : null;
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
                pins.Add(await IniEdits.Store(fileStore, path, bytes));
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
}
