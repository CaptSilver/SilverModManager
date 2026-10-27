using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.TestFramework.Helpers;

/// <summary>
/// Loadout groups a collection manifest can name: linked to a browser download or to a Nexus file. The library items are
/// written straight to the database, so a test needs no archive on disk unless it installs the mods.
/// </summary>
public static class ShareableMods
{
    /// <summary>A library file that was downloaded from a page, the kind a manifest writes with its md5.</summary>
    public static EntityId AddDownloadedFile(ITransaction tx, string fileName, Md5Value md5)
    {
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = fileName,
            Hash = fileName.xxHash3AsUtf8(),
            Size = Size.FromLong(1),
            Md5 = md5,
            LibraryItem = new LibraryItem.New(tx, id) { Name = fileName },
        };
        _ = new DownloadedFile.New(tx, id)
        {
            DownloadPageUri = new Uri("https://example.invalid/page"),
            LibraryFile = file,
        };
        return id;
    }

    /// <summary>A library item for a Nexus file the user never downloaded: ids and a name, no local file and so no md5.</summary>
    public static EntityId AddNexusItem(ITransaction tx, string name, NexusModsGameId game, GameDomain domain, uint modId, uint fileId)
    {
        var page = new NexusModsModPageMetadata.New(tx)
        {
            Uid = new ModUid(ModId.From(modId), game),
            Name = name,
            GameDomain = domain,
            UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
        };
        var file = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(FileId.From(fileId), game),
            Name = name,
            Version = "1",
            UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            ModPageId = page,
        };
        var item = new NexusModsLibraryItem.New(tx, out var id)
        {
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
            FileMetadataId = file.Id,
            ModPageMetadataId = page.Id,
        };
        return item.Id;
    }

    /// <summary>A child group of <paramref name="parent"/> installed from <paramref name="libraryItem"/>.</summary>
    public static LoadoutItemGroupId AddLinkedGroup(ITransaction tx, LoadoutId loadout, LoadoutItemGroupId parent, string name, EntityId libraryItem)
    {
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout, Name = name, ParentId = parent },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id) { LibraryItemId = libraryItem, LoadoutItemGroup = group };
        return group.Id;
    }

    public static void SetPriority(ITransaction tx, LoadoutId loadout, LoadoutItemGroupId group, ulong priority)
    {
        _ = new LoadoutItemGroupPriority.New(tx)
        {
            LoadoutId = loadout,
            TargetId = group,
            Priority = ConflictPriority.From(priority),
        };
    }
}
