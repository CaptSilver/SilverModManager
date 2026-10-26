using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;

/// <summary>
/// The changes the INI editor makes to a loadout: its copy of a game INI, its own tweak, and the switch on a tweak
/// file. Each one is a single transaction; the next write of the INI picks the change up.
/// </summary>
public static class IniEdits
{
    /// <summary>The group a loadout's own tweaks live in when the loadout has none yet.</summary>
    public const string GameSettingsGroupName = "Game settings";

    /// <summary>Makes <paramref name="bytes"/> the loadout's copy of the INI at <paramref name="path"/>, adding the row when there was none.</summary>
    public static async Task SetBaseline(IConnection connection, IFileStore fileStore, Loadout.ReadOnly loadout, GamePath path, byte[] bytes)
    {
        using var pin = await Store(fileStore, path, bytes);
        var hash = bytes.xxHash3();

        using var tx = connection.BeginTransaction();
        var state = LoadoutIniState.FindByLoadout(connection.Db, loadout.LoadoutId).Where(s => (GamePath)s.Path == path).ToList();
        if (state.Count > 0)
        {
            tx.Add(state[0].Id, LoadoutIniState.Baseline, hash);
        }
        else
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

    /// <summary>
    /// Sets the loadout's own tweak for <paramref name="target"/>. The file is created, together with the game
    /// settings group when the loadout has none, the first time and replaced in place after that.
    /// </summary>
    public static async Task SetLoadoutTweak(IConnection connection, IFileStore fileStore, Loadout.ReadOnly loadout, GamePath target, byte[] bytes)
    {
        var tweakPath = IniTweakLayers.LoadoutTweakPath(target.FileName.ToString());
        using var pin = await Store(fileStore, tweakPath, bytes);
        var hash = bytes.xxHash3();
        var size = Size.FromLong(bytes.Length);

        using var tx = connection.BeginTransaction();
        var existing = IniTweakLayers.All(loadout, target).Where(t => t.IsLoadoutTweak).ToList();
        if (existing.Count > 0)
        {
            tx.Add(existing[0].FileId, LoadoutFile.Hash, hash);
            tx.Add(existing[0].FileId, LoadoutFile.Size, size);
        }
        else
        {
            var group = GameSettingsGroupOf(connection, loadout) ?? NewGameSettingsGroup(tx, loadout);
            _ = new LoadoutFile.New(tx, out var fileId)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
                {
                    LoadoutItem = new LoadoutItem.New(tx, fileId)
                    {
                        LoadoutId = loadout.LoadoutId,
                        ParentId = group,
                        Name = tweakPath.FileName,
                    },
                    TargetPath = tweakPath.ToGamePathParentTuple(loadout.LoadoutId),
                },
                Hash = hash,
                Size = size,
            };
        }
        await tx.Commit();
    }

    /// <summary>Turns one tweak file on or off without touching the mod it belongs to.</summary>
    public static async Task SetTweakSwitch(IConnection connection, EntityId file, bool on)
    {
        using var tx = connection.BeginTransaction();
        if (on) tx.Retract(file, LoadoutItem.Disabled, Null.Instance);
        else tx.Add(file, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
    }

    private static LoadoutItemGroupId? GameSettingsGroupOf(IConnection connection, Loadout.ReadOnly loadout)
    {
        var groups = LoadoutItem.FindByLoadout(connection.Db, loadout.LoadoutId).OfTypeLoadoutItemGroup().Where(g => g.IsGameSettingsGroup).ToList();
        return groups.Count > 0 ? groups[0].LoadoutItemGroupId : null;
    }

    private static LoadoutItemGroupId NewGameSettingsGroup(ITransaction tx, Loadout.ReadOnly loadout)
    {
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id)
            {
                LoadoutId = loadout.LoadoutId,
                Name = GameSettingsGroupName,
            },
        };
        tx.Add(id, LoadoutItemGroup.GameSettingsGroup, Null.Instance);
        return group.LoadoutItemGroupId;
    }

    // The pin keeps the file store's collector from dropping the bytes between storing them and the commit that references them.
    private static async Task<IDisposable> Store(IFileStore fileStore, GamePath path, byte[] bytes)
    {
        var hash = bytes.xxHash3();
        var pin = FileStorePins.Pin(hash);
        try
        {
            if (!await fileStore.HaveFile(hash))
                await fileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.FileName, new MemoryStream(bytes)), hash, Size.FromLong(bytes.Length))]);
            return pin;
        }
        catch
        {
            pin.Dispose();
            throw;
        }
    }
}
