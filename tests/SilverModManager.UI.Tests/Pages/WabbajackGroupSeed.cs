using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>Writes the rows an install of a modlist leaves behind, one step at a time, so a test can stop at any of them.</summary>
internal static class WabbajackGroupSeed
{
    /// <summary>A modlist in the library, with the gallery identity the options give it.</summary>
    public static async Task<WabbajackModlistLibraryFile.ReadOnly> AddList(
        IConnection connection, string version, string? machineUrl = null, string repository = "wabbajack-tools/mod-lists", DateTimeOffset? dateUpdated = null,
        string? name = null, string gameType = "SkyrimSpecialEdition", Hash? xxHash64 = null, string? galleryUrl = null, string author = "Someone")
    {
        using var tx = connection.BeginTransaction();
        name ??= $"Update list {Guid.NewGuid():N}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + version);
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = $"{name}.wabbajack",
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = $"{name}.wabbajack" },
        };
        if (xxHash64 is { } hash) tx.Add(id, LibraryFile.XxHash64, hash);
        _ = new WabbajackModlistLibraryFile.New(tx, id)
        {
            LibraryFile = file, Name = name, Author = author, Version = version, GameType = gameType,
            ArchiveCount = 3, ArchiveSize = Size.From(5_000), InstallSize = Size.From(9_000), IsNsfw = false,
        };
        if (machineUrl is not null)
        {
            tx.Add(id, WabbajackModlistLibraryFile.MachineUrl, machineUrl);
            tx.Add(id, WabbajackModlistLibraryFile.Repository, repository);
        }
        if (dateUpdated is { } date) tx.Add(id, WabbajackModlistLibraryFile.GalleryDateUpdated, date);
        if (galleryUrl is not null) tx.Add(id, WabbajackModlistLibraryFile.GalleryDownloadUrl, galleryUrl);
        var result = await tx.Commit();
        return WabbajackModlistLibraryFile.Load(result.Db, result[id]);
    }

    /// <summary>What the install job writes when it switches a group to a new file: the new list, the old one as the previous list, a restore point, and the install mark off.</summary>
    public static async Task Switch(IConnection connection, EntityId group, EntityId from, EntityId to, bool finished = false)
    {
        using var tx = connection.BeginTransaction();
        var current = WabbajackModlistLoadoutGroup.Load(connection.Db, group);
        if (current.InstalledAt.HasValue) tx.Retract(group, WabbajackModlistLoadoutGroup.InstalledAt, current.InstalledAt.Value);
        tx.Retract(group, WabbajackModlistLoadoutGroup.Modlist, from);
        tx.Add(group, WabbajackModlistLoadoutGroup.Modlist, to);
        tx.Add(group, WabbajackModlistLoadoutGroup.PreviousModlist, from);
        tx.Add(group, WabbajackModlistLoadoutGroup.LastUpdateRestorePoint, EntityId.From(connection.Db.BasisTxId.Value));
        tx.Add(group, WabbajackModlistLoadoutGroup.WasDisabledByUser, false);
        await tx.Commit();
        if (finished) await Finish(connection, group);
    }

    /// <summary>The rows an undo of <see cref="Switch"/> leaves: the old list is current again and the update's marks are gone.</summary>
    public static async Task SwitchBack(IConnection connection, EntityId group, EntityId from, EntityId to)
    {
        using var tx = connection.BeginTransaction();
        var current = WabbajackModlistLoadoutGroup.Load(connection.Db, group);
        tx.Retract(group, WabbajackModlistLoadoutGroup.Modlist, to);
        tx.Add(group, WabbajackModlistLoadoutGroup.Modlist, from);
        if (WabbajackModlistLoadoutGroup.PreviousModlist.TryGetValue(current, out var previous)) tx.Retract(group, WabbajackModlistLoadoutGroup.PreviousModlist, previous);
        if (WabbajackModlistLoadoutGroup.LastUpdateRestorePoint.TryGetValue(current, out var restorePoint)) tx.Retract(group, WabbajackModlistLoadoutGroup.LastUpdateRestorePoint, restorePoint);
        if (WabbajackModlistLoadoutGroup.WasDisabledByUser.TryGetValue(current, out var disabled)) tx.Retract(group, WabbajackModlistLoadoutGroup.WasDisabledByUser, disabled);
        tx.Add(group, WabbajackModlistLoadoutGroup.InstalledAt, DateTimeOffset.UtcNow.AddDays(-1));
        await tx.Commit();
    }

    /// <summary>The group the install job creates: disabled, with no <see cref="WabbajackModlistLoadoutGroup.InstalledAt"/>.</summary>
    public static async Task<EntityId> AddGroup(IConnection connection, EntityId list, LoadoutId loadout, string name)
    {
        using var tx = connection.BeginTransaction();
        var group = new WabbajackModlistLoadoutGroup.New(tx, out var id)
        {
            ModlistId = list,
            CollectionGroup = new CollectionGroup.New(tx, id)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout, IsDisabled = true },
                },
            },
        };
        return (await tx.Commit()).Remap(group).Id;
    }

    public static async Task Finish(IConnection connection, EntityId group)
    {
        using var tx = connection.BeginTransaction();
        tx.Add(group, WabbajackModlistLoadoutGroup.InstalledAt, DateTimeOffset.UtcNow);
        await tx.Commit();
    }

    /// <summary>The user's toggle in the loadout's left menu.</summary>
    public static async Task SetEnabled(IConnection connection, EntityId group, bool enabled)
    {
        using var tx = connection.BeginTransaction();
        if (enabled) tx.Retract(group, LoadoutItem.Disabled, Null.Instance);
        else tx.Add(group, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
    }

    public static async Task<EntityId> AddSkip(IConnection connection, EntityId group, LoadoutId loadout, string to = "x/y.esp")
    {
        using var tx = connection.BeginTransaction();
        var skipped = new WabbajackSkippedFile.New(tx, out var id)
        {
            GroupId = WabbajackModlistLoadoutGroupId.From(group), To = to, Reason = "missing", Kind = SkipKind.MissingArchive,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = "skipped", LoadoutId = loadout },
        };
        return (await tx.Commit()).Remap(skipped).Id;
    }

    public static async Task Delete(IConnection connection, EntityId entity)
    {
        using var tx = connection.BeginTransaction();
        tx.Delete(entity, recursive: true);
        await tx.Commit();
    }
}
