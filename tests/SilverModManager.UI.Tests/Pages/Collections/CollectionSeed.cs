using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.MnemonicDB.Abstractions.IndexSegments;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.UI.Tests.Pages.Collections;

/// <summary>Rows an installed Nexus collection and a local collection leave behind, without installing anything.</summary>
internal static class CollectionSeed
{
    // The database outlives a test, so every seeded collection gets ids and a slug no other test uses.
    private static long _next = 70_000_000;

    internal sealed record NexusRows(CollectionRevisionMetadata.ReadOnly Revision, NexusCollectionLoadoutGroup.ReadOnly? Group);

    /// <summary>
    /// A collection revision as the library holds it. With <paramref name="withArchive"/> the collection's archive is
    /// in the library too, which is what lets its download page open.
    /// </summary>
    public static async Task<CollectionRevisionMetadata.ReadOnly> AddRevision(
        IConnection connection,
        NexusModsGameId game,
        string name,
        ulong revisionNumber = 1,
        bool withArchive = true,
        CollectionMetadata.ReadOnly? existing = null,
        Action<CollectionMetadata.New>? configureCollection = null,
        Action<CollectionRevisionMetadata.New>? configureRevision = null)
    {
        var id = (ulong)Interlocked.Increment(ref _next);
        using var tx = connection.BeginTransaction();

        EntityId collectionId;
        CollectionSlug slug;
        if (existing is { } known)
        {
            collectionId = known.Id;
            slug = known.Slug;
        }
        else
        {
            slug = CollectionSlug.From($"slug-{id}");
            var author = new User.New(tx) { Name = "An author", NexusId = id };
            var collection = new CollectionMetadata.New(tx)
            {
                CollectionId = CollectionId.From(id),
                Slug = slug,
                Name = name,
                GameId = game,
                AuthorId = author.Id,
            };
            configureCollection?.Invoke(collection);
            collectionId = collection.Id;
        }

        var revision = new CollectionRevisionMetadata.New(tx)
        {
            RevisionId = RevisionId.From(id),
            RevisionNumber = RevisionNumber.From(revisionNumber),
            CollectionId = collectionId,
        };
        configureRevision?.Invoke(revision);
        if (withArchive) AddArchive(tx, $"{name} {id}.collection", slug, RevisionNumber.From(revisionNumber));

        var result = await tx.Commit();
        return CollectionRevisionMetadata.Load(result.Db, result[revision.Id]);
    }

    /// <summary>An installed Nexus collection: the group the install job writes, on <paramref name="revision"/>.</summary>
    public static async Task<NexusCollectionLoadoutGroup.ReadOnly> AddInstalledGroup(
        IConnection connection, LoadoutId loadout, string name, CollectionRevisionMetadata.ReadOnly revision)
    {
        var archive = NexusModsCollectionLibraryFile.Load(connection.Db, connection.Db.Datoms(
            (NexusModsCollectionLibraryFile.CollectionSlug, revision.Collection.Slug),
            (NexusModsCollectionLibraryFile.CollectionRevisionNumber, revision.RevisionNumber)
        )[0]);

        using var tx = connection.BeginTransaction();
        var group = new NexusCollectionLoadoutGroup.New(tx, out var id)
        {
            CollectionId = revision.CollectionId,
            RevisionId = revision.Id,
            LibraryFileId = archive.Id,
            CollectionGroup = new CollectionGroup.New(tx, id)
            {
                IsReadOnly = true,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout },
                },
            },
        };
        var result = await tx.Commit();
        return result.Remap(group);
    }

    /// <summary>Points the installed group at another revision, as a collection update does.</summary>
    public static async Task SwitchRevision(IConnection connection, NexusCollectionLoadoutGroup.ReadOnly group, CollectionRevisionMetadata.ReadOnly revision)
    {
        using var tx = connection.BeginTransaction();
        tx.Add(group.Id, NexusCollectionLoadoutGroup.Revision, revision.Id);
        await tx.Commit();
    }

    /// <summary>A collection the user made: editable, with no Nexus rows.</summary>
    public static async Task<CollectionGroup.ReadOnly> AddLocalGroup(IConnection connection, LoadoutId loadout, string name)
    {
        using var tx = connection.BeginTransaction();
        var group = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout },
            },
        };
        var result = await tx.Commit();
        return result.Remap(group);
    }

    /// <summary>A modlist file in the library, with no install anywhere.</summary>
    public static async Task<WabbajackModlistLibraryFile.ReadOnly> AddModlistFile(
        IConnection connection,
        string name,
        string gameType = "SkyrimSpecialEdition",
        string version = "1.0",
        string author = "Someone",
        int archiveCount = 0,
        Size? archiveSize = null)
    {
        using var tx = connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        var file = new LibraryFile.New(tx, out var fileId)
        {
            FileName = $"{name}.wabbajack",
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, fileId) { Name = $"{name}.wabbajack" },
        };
        var modlist = new WabbajackModlistLibraryFile.New(tx, fileId)
        {
            LibraryFile = file,
            Name = name,
            Author = author,
            Version = version,
            GameType = gameType,
            ArchiveCount = archiveCount,
            ArchiveSize = archiveSize ?? Size.Zero,
            InstallSize = Size.Zero,
            IsNsfw = false,
        };
        var result = await tx.Commit();
        return WabbajackModlistLibraryFile.Load(result.Db, result[modlist.Id]);
    }

    /// <summary>
    /// A Wabbajack modlist in the library and its group in <paramref name="loadout"/>. A <paramref name="finished"/>
    /// group is enabled and has <c>InstalledAt</c>; otherwise it is the disabled group the job creates at the start.
    /// </summary>
    public static async Task<WabbajackModlistLoadoutGroup.ReadOnly> AddModlistGroup(
        IConnection connection, LoadoutId loadout, string name, bool finished = false)
    {
        var list = await AddModlistFile(connection, name);
        using var tx = connection.BeginTransaction();
        var group = new WabbajackModlistLoadoutGroup.New(tx, out var id)
        {
            ModlistId = list.Id,
            CollectionGroup = new CollectionGroup.New(tx, id)
            {
                IsReadOnly = false,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout, IsDisabled = finished is false },
                },
            },
        };
        if (finished) tx.Add(id, WabbajackModlistLoadoutGroup.InstalledAt, DateTimeOffset.UtcNow);
        var result = await tx.Commit();
        return result.Remap(group);
    }

    private static void AddArchive(ITransaction tx, string fileName, CollectionSlug slug, RevisionNumber revision)
    {
        var libraryFile = new LibraryFile.New(tx, out var id)
        {
            FileName = fileName,
            Hash = fileName.xxHash3AsUtf8(),
            Size = Size.FromLong(fileName.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = fileName },
        };
        _ = new LibraryArchive.New(tx, id) { LibraryFile = libraryFile, IsArchive = true };
        _ = new NexusModsCollectionLibraryFile.New(tx, id)
        {
            LibraryFile = libraryFile,
            CollectionSlug = slug,
            CollectionRevisionNumber = revision,
        };
    }
}
