using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Collections;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Games.RedEngine.Cyberpunk2077;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.DataModel.Tests;

/// <summary>
/// Cyberpunk-backed test base that can add offline stand-ins for installed Nexus Mods
/// collections. The entities have the same shape the collection installer writes: a
/// <see cref="NexusCollectionLoadoutGroup"/> whose children are
/// <see cref="NexusCollectionItemLoadoutGroup"/>s that are also <see cref="LibraryLinkedLoadoutItem"/>s.
/// </summary>
public abstract class ANexusCollectionTest<TTest>(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<TTest>(helper)
{
    private static long _nextNexusId = 1_000;

    /// <summary>
    /// Adds an installed collection to the loadout in a single transaction.
    /// With <paramref name="duplicateOf"/> the new group shares that group's collection, revision and
    /// library file instead of creating its own; the id parameters are then unused.
    /// </summary>
    /// <param name="loadout">Target loadout.</param>
    /// <param name="name">Name of the collection group; child groups are named after it.</param>
    /// <param name="collectionId">Nexus collection id, unique per collection.</param>
    /// <param name="revisionId">Nexus revision id, unique per revision.</param>
    /// <param name="childCount">
    /// Number of library-linked children. The first one is also a replicated group.
    /// </param>
    /// <param name="withBundledChild">
    /// Adds a bundled child with three files, two of them identical. Bundled children are not library-linked,
    /// so they do not count towards a revision's mod count.
    /// </param>
    protected async Task<NexusCollectionLoadoutGroup.ReadOnly> AddNexusCollection(
        LoadoutId loadout,
        string name,
        ulong collectionId,
        ulong revisionId,
        int childCount,
        bool withBundledChild = false,
        NexusCollectionLoadoutGroup.ReadOnly? duplicateOf = null)
    {
        using var tx = Connection.BeginTransaction();

        EntityId collectionEntity, revisionEntity, libraryFileId;
        if (duplicateOf is { } original)
        {
            collectionEntity = NexusCollectionLoadoutGroup.CollectionId.Get(original);
            revisionEntity = NexusCollectionLoadoutGroup.RevisionId.Get(original);
            libraryFileId = NexusCollectionLoadoutGroup.LibraryFileId.Get(original);
        }
        else
        {
            (collectionEntity, revisionEntity) = AddCollectionMetadata(tx, name, collectionId, revisionId);
            libraryFileId = AddLibraryFile(tx, $"{name}.collection", new NexusModsCollectionExtras(CollectionSlug.From($"slug-{collectionId}"), RevisionNumber.From(1)));
        }

        var group = AddCollectionGroup(tx, loadout, name, collectionEntity, revisionEntity, libraryFileId);

        for (var i = 0; i < childCount; i++)
        {
            var childName = $"{name} mod {i}";
            var download = new CollectionDownload.New(tx)
            {
                Name = childName,
                IsOptional = false,
                ArrayIndex = i,
                CollectionRevisionId = revisionEntity,
            };
            var libraryItemId = AddLibraryFile(tx, $"{childName}.zip");

            var itemGroup = new LoadoutItemGroup.New(tx, out var id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    Name = childName,
                    LoadoutId = loadout,
                    ParentId = group.Id,
                },
            };

            var collectionItem = new NexusCollectionItemLoadoutGroup.New(tx, id)
            {
                DownloadId = download.Id,
                IsRequired = true,
                LoadoutItemGroup = itemGroup,
            };

            if (i == 0)
            {
                _ = new NexusCollectionReplicatedLoadoutGroup.New(tx, id)
                {
                    IsReplicated = true,
                    NexusCollectionItemLoadoutGroup = collectionItem,
                };
            }

            _ = new LibraryLinkedLoadoutItem.New(tx, id)
            {
                LibraryItemId = libraryItemId,
                LoadoutItemGroup = collectionItem.GetLoadoutItemGroup(tx),
            };

            AddFile(tx, loadout, LoadoutItemGroupId.From(id.Value), new GamePath(LocationId.Game, $"{name}/{i}.txt"));
        }

        if (withBundledChild)
        {
            var bundledName = $"{name} bundled";
            var bundled = new CollectionDownloadBundled.New(tx, out var bundledId)
            {
                BundledPath = "b1",
                CollectionDownload = new CollectionDownload.New(tx, bundledId)
                {
                    Name = bundledName,
                    IsOptional = false,
                    ArrayIndex = childCount,
                    CollectionRevisionId = revisionEntity,
                },
            };

            var bundledGroup = new NexusCollectionBundledLoadoutGroup.New(tx, out var id)
            {
                CollectionLibraryFileId = libraryFileId,
                BundleDownloadId = bundled.Id,
                NexusCollectionItemLoadoutGroup = new NexusCollectionItemLoadoutGroup.New(tx, id)
                {
                    DownloadId = CollectionDownloadId.From(bundledId.Value),
                    IsRequired = true,
                    LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                    {
                        IsGroup = true,
                        LoadoutItem = new LoadoutItem.New(tx, id)
                        {
                            Name = bundledName,
                            LoadoutId = loadout,
                            ParentId = group.Id,
                        },
                    },
                },
            };

            var bundledGroupId = LoadoutItemGroupId.From(bundledGroup.Id.Value);
            AddFile(tx, loadout, bundledGroupId, new GamePath(LocationId.Game, $"{name}/bundled/a.txt"), "bundled content a");
            AddFile(tx, loadout, bundledGroupId, new GamePath(LocationId.Game, $"{name}/bundled/b.txt"), "bundled content bb");
            AddFile(tx, loadout, bundledGroupId, new GamePath(LocationId.Game, $"{name}/bundled/c.txt"), "bundled content a");
        }

        var result = await tx.Commit();
        return result.Remap(group);
    }

    /// <summary>
    /// Adds a group holding one library-linked item that is not part of any collection.
    /// </summary>
    protected async Task<LoadoutItemGroupId> AddLinkedItem(LoadoutId loadout, string name)
    {
        var archive = await CreateLibraryArchive(Connection, $"{name}.zip");
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id)
        {
            LibraryItemId = archive.Id,
            LoadoutItemGroup = group,
        };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[id].Value);
    }

    /// <summary>
    /// Adds a group holding a single <c>mods/{name}/info.json</c> file, which is what makes it a REDmod.
    /// </summary>
    protected async Task<LoadoutItemGroupId> AddRedMod(LoadoutId loadout, string name)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout, name);
        AddFile(tx, loadout, group, new GamePath(LocationId.Game, $"mods/{name}/info.json"));
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group].Value);
    }

    /// <summary>
    /// Every datom currently asserted on the given entities, as (entity, attribute, value) with the value
    /// as hex so that set equality compares contents. The transaction is left out on purpose: a datom that
    /// was retracted and asserted again with the same value is not a change.
    /// </summary>
    protected static HashSet<(EntityId E, AttributeId A, string V)> DatomsOf(IDb db, IEnumerable<EntityId> ids)
    {
        var result = new HashSet<(EntityId, AttributeId, string)>();
        foreach (var id in ids)
        {
            foreach (var datom in db.Datoms(id))
                result.Add((datom.E, datom.A, $"{datom.Prefix.ValueTag}:{Convert.ToHexString(datom.ValueSpan)}"));
        }
        return result;
    }

    /// <summary>
    /// The entities the loadout owns in <paramref name="db"/>, excluding the loadout row itself.
    /// </summary>
    protected HashSet<EntityId> OwnedIds(IDb db, EntityId loadout)
    {
        return Connection
            .Query<EntityId>($"SELECT DISTINCT Id FROM loadouts.TrackedEntitiesForLoadout({db}, {loadout}) WHERE Id != {loadout}")
            .ToHashSet();
    }

    /// <summary>
    /// Records <see cref="Loadout.LastAppliedDateTime"/> on the loadout, which is what makes the undo
    /// service list a revision.
    /// </summary>
    protected async Task StampRevision(EntityId loadout)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(loadout, Loadout.LastAppliedDateTime, DateTime.UtcNow);
        await tx.Commit();
    }

    private readonly record struct NexusModsCollectionExtras(CollectionSlug Slug, RevisionNumber Revision);

    private (EntityId Collection, EntityId Revision) AddCollectionMetadata(ITransaction tx, string name, ulong collectionId, ulong revisionId)
    {
        var author = new User.New(tx)
        {
            Name = "author",
            NexusId = (ulong)Interlocked.Increment(ref _nextNexusId),
        };

        var collection = new CollectionMetadata.New(tx)
        {
            CollectionId = CollectionId.From(collectionId),
            Slug = CollectionSlug.From($"slug-{collectionId}"),
            Name = name,
            GameId = Cyberpunk2077Game.NexusModsGameId.Value,
            AuthorId = author.Id,
        };

        var revision = new CollectionRevisionMetadata.New(tx)
        {
            RevisionId = RevisionId.From(revisionId),
            RevisionNumber = RevisionNumber.From(1),
            CollectionId = collection.Id,
        };

        return (collection.Id, revision.Id);
    }

    private static EntityId AddLibraryFile(ITransaction tx, string fileName, NexusModsCollectionExtras? collection = null)
    {
        var libraryFile = new LibraryFile.New(tx, out var id)
        {
            FileName = fileName,
            Hash = fileName.xxHash3AsUtf8(),
            Size = Size.FromLong(fileName.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = fileName },
        };

        _ = new LibraryArchive.New(tx, id)
        {
            LibraryFile = libraryFile,
            IsArchive = true,
        };

        if (collection is { } extras)
        {
            _ = new NexusModsCollectionLibraryFile.New(tx, id)
            {
                LibraryFile = libraryFile,
                CollectionSlug = extras.Slug,
                CollectionRevisionNumber = extras.Revision,
            };
        }

        return id;
    }

    private static NexusCollectionLoadoutGroup.New AddCollectionGroup(
        ITransaction tx, LoadoutId loadout, string name, EntityId collection, EntityId revision, EntityId libraryFileId)
    {
        return new NexusCollectionLoadoutGroup.New(tx, out var id)
        {
            CollectionId = collection,
            RevisionId = revision,
            LibraryFileId = libraryFileId,
            CollectionGroup = new CollectionGroup.New(tx, id)
            {
                IsReadOnly = true,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id)
                    {
                        Name = name,
                        LoadoutId = loadout,
                    },
                },
            },
        };
    }
}
