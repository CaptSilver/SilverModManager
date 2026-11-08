using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
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
    /// Refuses a section, key or value <see cref="IniDocument"/> could not write, before anything is changed.
    /// <paramref name="value"/> is null when the change has none (a removal).
    /// </summary>
    /// <exception cref="IniTweakRefusedException">The section, key or value cannot be written to an INI.</exception>
    public static void CheckTweakName(string section, string key, string? value)
    {
        if (HasLineBreak(section) || section.Contains(']'))
            throw new IniTweakRefusedException($"{MarkdownText.Code(section)} is not a section name: it cannot contain ']' or a line break.");
        if (key.Length == 0 || key.Contains('=') || HasLineBreak(key))
            throw new IniTweakRefusedException($"{MarkdownText.Code(key)} is not a key name: it cannot be empty or contain '=' or a line break.");
        if (value is not null && HasLineBreak(value))
            throw new IniTweakRefusedException("A value cannot span lines.");
    }

    private static bool HasLineBreak(string text) => text.Contains('\n') || text.Contains('\r');

    /// <summary>
    /// The user's tweak of <paramref name="target"/> as a document, with the identity it was read from: an empty
    /// document and a null identity when the loadout has none.
    /// </summary>
    /// <exception cref="IniTweakRefusedException">The tweak's bytes are not in the file store.</exception>
    public static async Task<(IniDocument Document, FileIdentity? Identity)> LoadLoadoutTweak(IFileStore fileStore, Loadout.ReadOnly loadout, GamePath target)
    {
        if (IniTweakLayers.UserTweak(loadout, target) is not { } user) return (IniDocument.CreateEmpty(), null);
        if (!await fileStore.HaveFile(user.Identity)) throw GoneLayer(user.Name, user.SourceName, target);
        return (IniDocument.Parse(await fileStore.Load(user.Identity)), user.Identity);
    }

    /// <summary>
    /// Sets the loadout's own tweak for <paramref name="target"/>. The file is created, together with the game
    /// settings group when the loadout has none, the first time and replaced in place after that.
    /// </summary>
    /// <param name="expected">
    /// The identity of the user's tweak the caller read, null for "there was none". When the tweak is different now
    /// something else changed it since, and nothing is written.
    /// </param>
    /// <exception cref="IniTweakRefusedException">The tweak is not the one the caller read.</exception>
    public static async Task SetLoadoutTweak(IConnection connection, IFileStore fileStore, Loadout.ReadOnly loadout, GamePath target, byte[] bytes, FileIdentity? expected)
    {
        var tweakPath = IniTweakLayers.LoadoutTweakPath(target.FileName.ToString());
        using var pin = await Store(fileStore, tweakPath, bytes);
        var hash = bytes.xxHash3();
        var size = Size.FromLong(bytes.Length);

        using var tx = connection.BeginTransaction();
        // The caller's snapshot may be old, so the tweak is read again from the database the write goes to.
        var current = Loadout.Load(connection.Db, loadout.LoadoutId);
        if (current.IsValid()) loadout = current;
        var existing = IniTweakLayers.UserTweak(loadout, target);
        if (!Equals(existing?.Identity, expected))
            throw new IniTweakRefusedException($"The loadout tweak of {MarkdownText.Code(target.FileName.ToString())} changed since it was read, by the command line or another window. It was reloaded; make the change again.");

        if (existing is { } user)
        {
            tx.Add(user.FileId, LoadoutFile.Hash, hash);
            tx.Add(user.FileId, LoadoutFile.Size, size);
        }
        else
        {
            var group = UserSettingsGroup(connection.Db, tx, loadout);
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

    /// <summary>
    /// Sets one key in the user's tweak of <paramref name="target"/>, keeping the rest of the file and its encoding.
    /// The loadout is read again from the database, so the caller needs no current snapshot.
    /// </summary>
    /// <returns>The value the tweak had for the key, null when it did not set it.</returns>
    /// <exception cref="IniTweakRefusedException">The name or value cannot be written, or the tweak changed while it was edited.</exception>
    public static async Task<string?> SetLoadoutTweakKey(IConnection connection, IFileStore fileStore, LoadoutId loadoutId, GamePath target, string section, string key, string value)
    {
        CheckTweakName(section, key, value);
        var loadout = Loadout.Load(connection.Db, loadoutId);
        var (tweak, identity) = await LoadLoadoutTweak(fileStore, loadout, target);

        var old = tweak.Get(section, key);
        tweak.Set(section, key, value);
        await SetLoadoutTweak(connection, fileStore, loadout, target, tweak.ToBytes(), identity);
        return old;
    }

    /// <summary>
    /// Removes every line of the user's tweak of <paramref name="target"/> that sets the key (a hand-written tweak may
    /// repeat one). Nothing is written when none does.
    /// </summary>
    /// <returns>Whether the tweak set the key.</returns>
    /// <exception cref="IniTweakRefusedException">The name cannot be written, or the tweak changed while it was edited.</exception>
    public static async Task<bool> RemoveLoadoutTweakKey(IConnection connection, IFileStore fileStore, LoadoutId loadoutId, GamePath target, string section, string key)
    {
        CheckTweakName(section, key, null);
        var loadout = Loadout.Load(connection.Db, loadoutId);
        var (tweak, identity) = await LoadLoadoutTweak(fileStore, loadout, target);

        var removed = false;
        while (tweak.Remove(section, key)) removed = true;
        if (!removed) return false;

        await SetLoadoutTweak(connection, fileStore, loadout, target, tweak.ToBytes(), identity);
        return true;
    }

    /// <summary>The refusal for a tweak layer whose bytes the file store lost.</summary>
    internal static IniTweakRefusedException GoneLayer(string name, string sourceName, GamePath target) =>
        new($"The tweak {MarkdownText.Code(name)} from {MarkdownText.Code(sourceName)} for {MarkdownText.Code(target.FileName.ToString())} has no stored bytes any more (a restore point older than the last Apply was restored). Switch it off or set it again.");

    /// <summary>Turns one tweak file on or off without touching the mod it belongs to.</summary>
    public static async Task SetTweakSwitch(IConnection connection, EntityId file, bool on)
    {
        using var tx = connection.BeginTransaction();
        if (on) tx.RetractCurrent(file, LoadoutItem.Disabled);
        else tx.Add(file, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
    }

    /// <summary>
    /// The group that holds the user's own tweaks: the loadout's game settings group, made in <paramref name="tx"/> when
    /// there is none. A settings group inside another group (the one a modlist install keeps its own tweak in) belongs to
    /// that group's installer, which deletes what it did not write, so it never counts.
    /// </summary>
    public static LoadoutItemGroupId UserSettingsGroup(IDb db, ITransaction tx, Loadout.ReadOnly loadout) =>
        GameSettingsGroupOf(db, loadout) ?? NewGameSettingsGroup(tx, loadout);

    /// <summary>The loadout's own game settings group, or null when it has none.</summary>
    public static LoadoutItemGroupId? GameSettingsGroupOf(IDb db, Loadout.ReadOnly loadout)
    {
        var groups = LoadoutItem.FindByLoadout(db, loadout.LoadoutId).OfTypeLoadoutItemGroup()
            .Where(IniTweakLayers.IsUsersSettingsGroup)
            .ToList();
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

    /// <summary>
    /// Puts <paramref name="bytes"/> in the file store. The pin keeps the store's collector from dropping them between
    /// storing and the commit that references them; dispose it after that commit.
    /// </summary>
    public static async Task<IDisposable> Store(IFileStore fileStore, GamePath path, byte[] bytes)
    {
        var hash = bytes.xxHash3();
        var pin = FileStorePins.Pin(FileIdentity.XxHash3(hash));
        try
        {
            if (!await fileStore.HaveFile(FileIdentity.XxHash3(hash)))
                await fileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path.FileName, new MemoryStream(bytes)), FileIdentity.XxHash3(hash), Size.FromLong(bytes.Length))]);
            return pin;
        }
        catch
        {
            pin.Dispose();
            throw;
        }
    }
}
