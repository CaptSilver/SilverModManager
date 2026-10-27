using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DynamicData.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Networking.NexusWebApi.Errors;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Library;
using CollectionMod = SilverModManager.Abstractions.Collections.Json.Mod;
using ModSource = SilverModManager.Abstractions.Collections.Json.ModSource;
using UpdatePolicy = SilverModManager.Abstractions.Collections.Json.UpdatePolicy;

namespace SilverModManager.Collections;

public static class CollectionCreator
{
    public static bool IsCollectionUploaded(IConnection connection, CollectionGroupId groupId, out CollectionMetadata.ReadOnly collection)
    {
        var group = ManagedCollectionLoadoutGroup.Load(connection.Db, groupId);
        if (!group.IsValid())
        {
            collection = default(CollectionMetadata.ReadOnly);
            return false;
        }

        collection = group.Collection;
        return true;
    }

    private static string GenerateNewCollectionName(string[] allNames)
    {
        const string defaultValue = "My new collection";
        const string template = "({0}) My new collection";
        var count = 1;
        var current = allNames.Any(x => x.SequenceEqual(defaultValue)) ? TemplatedName() : defaultValue;

        foreach (var existingName in allNames.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (existingName.SequenceEqual(current)) current = TemplatedName();
        }

        return current;
        string TemplatedName() => string.Format(template, ++count);
    }

    public static async Task<EntityId> MakeEditableLocalCollection(
        ILoadoutManager loadoutManager,
        IConnection conn,
        CollectionGroupId collectionGroupId,
        string newName)
    {
        var cloneId = await loadoutManager.CloneCollection(collectionGroupId);
        var cloneEnt = NexusCollectionLoadoutGroup.Load(conn.Db, cloneId);

        using var tx = conn.BeginTransaction();
        // Remap the name
        tx.Add(cloneId, LoadoutItem.Name, newName);
        // Make it editable
        tx.Add(cloneId, CollectionGroup.IsReadOnly, false);
        // Retract the Nexus references as this is no longer associated with the official collection
        tx.Retract(cloneId, NexusCollectionLoadoutGroup.RevisionId, NexusCollectionLoadoutGroup.RevisionId.Get(cloneEnt));
        tx.Retract(cloneId, NexusCollectionLoadoutGroup.CollectionId, NexusCollectionLoadoutGroup.CollectionId.Get(cloneEnt));
        tx.Retract(cloneId, NexusCollectionLoadoutGroup.LibraryFileId, NexusCollectionLoadoutGroup.LibraryFileId.Get(cloneEnt));

        // Retract the Nexus references in items so that this is no longer associated with the official collection
        var db = conn.Db;
        foreach (var item in conn.Query<EntityId>($"SELECT Id FROM mdb_NexusCollectionItemLoadoutGroup(Db=>{db}) WHERE Parent = {cloneId.Id}"))
        {
            var ent = NexusCollectionItemLoadoutGroup.Load(db, item);
            tx.Retract(item, NexusCollectionItemLoadoutGroup.Download, NexusCollectionItemLoadoutGroup.Download.Get(ent));
            tx.Retract(item, NexusCollectionItemLoadoutGroup.IsRequired, NexusCollectionItemLoadoutGroup.IsRequired.Get(ent));

            if (NexusCollectionReplicatedLoadoutGroup.Replicated.TryGetValue(ent, out var replicated))
                tx.Retract(item, NexusCollectionReplicatedLoadoutGroup.Replicated, replicated);

            if (NexusCollectionBundledLoadoutGroup.CollectionLibraryFileId.TryGetValue(ent, out var bundleLibraryFileId))
                tx.Retract(item, NexusCollectionBundledLoadoutGroup.CollectionLibraryFileId, bundleLibraryFileId);

            if (NexusCollectionBundledLoadoutGroup.BundleDownload.TryGetValue(ent, out var bundleDownload))
            {
                tx.Retract(item, NexusCollectionBundledLoadoutGroup.BundleDownload, bundleDownload);

                // We've now orphaned the bundled files, so we'll now create a download archive that contains the files this loadout group needs.

                var fileName = "Bundled Files - " + ent.AsLoadoutItemGroup().AsLoadoutItem().Name;

                // Don't add duplicates: one archive entry per distinct hash.
                var distinctFiles = ent.AsLoadoutItemGroup().Children
                    .OfTypeLoadoutItemWithTargetPath()
                    .OfTypeLoadoutFile()
                    .DistinctBy(static child => child.Hash)
                    .ToArray();

                var sum = Size.Zero;
                foreach (var child in distinctFiles) sum += child.Size;

                // Create a new library item. The size has to be known up front: adding a second
                // LibraryFile.Size to the same entity in this transaction leaves both values in the
                // database and readers see the first one.
                var libraryFile = new ManuallyCreatedArchive.New(tx, out var libraryFileId)
                {
                    Source = ManuallyCreatedArchive.CreationSource.CollectionBundled,
                    LibraryArchive = new LibraryArchive.New(tx, libraryFileId)
                    {
                        IsArchive = true,
                        LibraryFile = new LibraryFile.New(tx, libraryFileId)
                        {
                            Hash = Hash.Zero,
                            Size = sum,
                            FileName = fileName,
                            LibraryItem = new LibraryItem.New(tx, libraryFileId)
                            {
                                Name = fileName,
                            },
                        },

                    },
                };

                // Link the mod group to the archive
                tx.Add(item, LibraryLinkedLoadoutItem.LibraryItemId, libraryFileId);

                foreach (var child in distinctFiles)
                {
                    var name = child.AsLoadoutItemWithTargetPath().AsLoadoutItem().Name;
                    _ = new LibraryArchiveFileEntry.New(tx, out var fileId)
                    {
                        ParentId = libraryFileId,
                        Path = child.AsLoadoutItemWithTargetPath().TargetPath.Item3,
                        LibraryFile = new LibraryFile.New(tx, fileId)
                        {
                            Hash = child.Hash,
                            Size = child.Size,
                            FileName = name,
                            LibraryItem = new LibraryItem.New(tx, fileId)
                            {
                                Name = name,
                            },
                        },
                    };
                }
            }

        }

        await tx.Commit();
        return cloneId;
    }

    /// <summary>
    /// Creates a new collection group in the loadout.
    /// </summary>
    public static async ValueTask<CollectionGroup.ReadOnly> CreateNewCollectionGroup(IConnection connection, LoadoutId loadoutId, string newName)
    {
        var loadout = Loadout.Load(connection.Db, loadoutId);

        if (string.IsNullOrWhiteSpace(newName))
        {
            var names = loadout.MutableCollections().Select(x => x.Name).ToArray();
            newName = GenerateNewCollectionName(names);
        }

        using var tx = connection.BeginTransaction();

        var group = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    Name = newName,
                    LoadoutId = loadoutId,
                },
            },
        };

        var result = await tx.Commit();

        return result.Remap(group);
    }

    private static async ValueTask<(IStreamFactory, TemporaryPath, CollectionRoot)> PrepareForUpload(
        IServiceProvider serviceProvider,
        CollectionGroupId groupId,
        CancellationToken cancellationToken)
    {
        var connection = serviceProvider.GetRequiredService<IConnection>();
        var loginManager = serviceProvider.GetRequiredService<ILoginManager>();
        var nexusModsLibrary = serviceProvider.GetRequiredService<NexusModsLibrary>();
        var temporaryFileManager = serviceProvider.GetRequiredService<TemporaryFileManager>();
        var jsonSerializerOptions = serviceProvider.GetRequiredService<JsonSerializerOptions>();
        var mappingCache = serviceProvider.GetRequiredService<IGameDomainToGameIdMappingCache>();
        var fileHashesService = serviceProvider.GetRequiredService<IFileHashesService>();

        var userInfo = loginManager.UserInfo;
        if (userInfo is null) throw new NotSupportedException("User has to be logged in!");

        var users = User.FindByNexusId(connection.Db, userInfo.UserId.Value);
        if (!users.TryGetFirst(out var user)) throw new NotSupportedException("User has to be logged in!");

        var group = CollectionGroup.Load(connection.Db, groupId);
        Debug.Assert(group.IsValid());

        await fileHashesService.GetFileHashesDb();
        var installation = group.AsLoadoutItemGroup().AsLoadoutItem().Loadout.InstallationInstance;
        var locatorIds = installation.LocatorResult.LocatorIds.ToArray();
        var vanityVersion = fileHashesService.TryGetVanityVersion((installation.LocatorResult.Store, locatorIds), out var tmpVanityVersion) ? tmpVanityVersion : VanityVersion.From("Unknown");

        var shared = BuildManifest(
            group: group.AsLoadoutItemGroup(),
            mappingCache: mappingCache,
            author: user,
            vanityVersion
        );
        var collectionManifest = shared.Manifest;

        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(CollectionCreator));
        foreach (var skipped in shared.Skipped)
            logger.LogTrace("'{Mod}' is not in the shared collection: it was not downloaded from Nexus Mods or a download page, so a manifest cannot name it", skipped.AsLoadoutItem().Name);

        var archiveFile = temporaryFileManager.CreateFile(ext: Extension.FromPath(".zip"));
        var streamFactory = await CreateArchive(archiveFile, jsonSerializerOptions, collectionManifest, cancellationToken);

        return (streamFactory, archiveFile, collectionManifest);
    }

    public static async ValueTask<GraphQlResult<Abstractions.NexusModsLibrary.Models.CollectionStatus, NotFound, CollectionDiscarded>> ChangeCollectionStatus(
        IServiceProvider serviceProvider,
        ManagedCollectionLoadoutGroupId groupId,
        Abstractions.NexusModsLibrary.Models.CollectionStatus newStatus,
        CancellationToken cancellationToken)
    {
        var nexusModsLibrary = serviceProvider.GetRequiredService<NexusModsLibrary>();
        var connection = serviceProvider.GetRequiredService<IConnection>();

        var managedGroup = ManagedCollectionLoadoutGroup.Load(connection.Db, groupId);
        var collection = managedGroup.Collection;

        if (collection.Status == newStatus) return newStatus;

        var result = await nexusModsLibrary.ChangeCollectionStatus(
            collection.CollectionId,
            newStatus: newStatus,
            cancellationToken: cancellationToken
        );

        if (result.HasErrors) return newStatus;
        using var tx = connection.BeginTransaction();

        tx.Add(collection.Id, CollectionMetadata.Status, newStatus);

        await tx.Commit();
        return newStatus;
    }

    /// <summary>
    /// Creates a new collection.
    /// </summary>
    public static async ValueTask<CollectionMetadata.ReadOnly> CreateCollection(
        IServiceProvider serviceProvider,
        CollectionGroupId groupId,
        Abstractions.NexusModsLibrary.Models.CollectionStatus initialCollectionStatus = default,
        CancellationToken cancellationToken = default)
    {
        var nexusModsLibrary = serviceProvider.GetRequiredService<NexusModsLibrary>();
        var connection = serviceProvider.GetRequiredService<IConnection>();

        var (streamFactory, archiveFile, collectionManifest) = await PrepareForUpload(serviceProvider, groupId, cancellationToken);
        await using var _ = archiveFile;

        var group = CollectionGroup.Load(connection.Db, groupId);
        using var tx = connection.BeginTransaction();

        var (collection, revisionId) = await nexusModsLibrary.CreateCollection(streamFactory, collectionManifest, cancellationToken);
        var uploadTime = DateTimeOffset.UtcNow;

        var defaultImageStreamFactory = new EmbeddedResourceStreamFactory<CollectionDownloader>("SilverModManager.Collections.default-collection-image.webp");
        var defaultImageMimeType = "image/webp";

        var prefillResult = await nexusModsLibrary.PrefillCollectionMetadata(collection, defaultImageStreamFactory, defaultImageMimeType, cancellationToken);

        // TODO: handle result
        prefillResult.AssertHasData();

        var result = await nexusModsLibrary.PublishRevision(revisionId, cancellationToken);

        // TODO: handle result
        result.AssertHasData();

        tx.Add(groupId, ManagedCollectionLoadoutGroup.Collection, collection);
        tx.Add(groupId, ManagedCollectionLoadoutGroup.CurrentRevisionNumber, RevisionNumber.From(2));
        tx.Add(groupId, ManagedCollectionLoadoutGroup.LastUploadDate, uploadTime);
        tx.Add(groupId, ManagedCollectionLoadoutGroup.LastPublishedRevisionNumber, RevisionNumber.From(1));

        await tx.Commit();

        if (initialCollectionStatus is not Abstractions.NexusModsLibrary.Models.CollectionStatus.Unlisted)
        {
            await ChangeCollectionStatus(serviceProvider, groupId.Value, newStatus: initialCollectionStatus, cancellationToken);
        }

        return CollectionMetadata.Load(connection.Db, collection.Id);
    }

    public static async ValueTask<CollectionMetadata.ReadOnly> UploadAndPublishRevision(
        IServiceProvider serviceProvider,
        ManagedCollectionLoadoutGroupId groupId,
        CancellationToken cancellationToken)
    {
        var nexusModsLibrary = serviceProvider.GetRequiredService<NexusModsLibrary>();
        var connection = serviceProvider.GetRequiredService<IConnection>();
        var client = serviceProvider.GetRequiredService<IGraphQlClient>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger(nameof(CollectionCreator));

        var managedGroup = ManagedCollectionLoadoutGroup.Load(connection.Db, groupId);

        var (collection, revisionId, revisionNumber) = await UploadDraftRevision(serviceProvider, groupId, cancellationToken);

        var previousRevisionResult = await client.QueryCollectionRevision(collection.Slug, RevisionNumber.From(revisionNumber.Value - 1), cancellationToken: cancellationToken);
        var currentRevisionResult = await client.QueryCollectionRevision(collection.Slug, revisionNumber, cancellationToken);

        if (currentRevisionResult.HasData)
        {
            var changelog = NexusModsLibrary.GenerateChangelog(currentRevisionResult.AssertHasData(), previousRevisionResult.HasData ? Optional<ICollectionRevision>.Create(previousRevisionResult.AssertHasData()) : Optional<ICollectionRevision>.None);
            if (changelog is not null)
            {
                var changelogResult = await client.CreateChangelog(revisionId, changelog: changelog, cancellationToken: cancellationToken);

                if (changelogResult.TryGetError(out Invalid? invalid))
                {
                    logger.LogWarning("Failed to create changelog for `{Slug}/{RevisionNumber}` because of invalid input: {Message}", collection.Slug, revisionNumber, invalid.Message);
                }
                else
                {
                    _ = changelogResult.AssertHasData();
                }
            }
        }

        var result = await nexusModsLibrary.PublishRevision(revisionId, cancellationToken);

        // TODO: handle result
        _ = result.AssertHasData();

        using var tx = connection.BeginTransaction();
        tx.Add(groupId, ManagedCollectionLoadoutGroup.LastPublishedRevisionNumber, managedGroup.CurrentRevisionNumber);
        tx.Add(groupId, ManagedCollectionLoadoutGroup.CurrentRevisionNumber, RevisionNumber.From(managedGroup.CurrentRevisionNumber.Value + 1));
        tx.Add(groupId, ManagedCollectionLoadoutGroup.LastUploadDate, DateTimeOffset.UtcNow);
        if (managedGroup.CurrentRevisionId.HasValue)
            tx.Retract(groupId, ManagedCollectionLoadoutGroup.CurrentRevisionId, managedGroup.CurrentRevisionId.Value);

        await tx.Commit();
        return collection;
    }

    public static async ValueTask<(CollectionMetadata.ReadOnly, RevisionId, RevisionNumber)> UploadDraftRevision(
        IServiceProvider serviceProvider,
        ManagedCollectionLoadoutGroupId groupId,
        CancellationToken cancellationToken)
    {
        var nexusModsLibrary = serviceProvider.GetRequiredService<NexusModsLibrary>();
        var connection = serviceProvider.GetRequiredService<IConnection>();

        var (streamFactory, archiveFile, collectionManifest) = await PrepareForUpload(serviceProvider, groupId.Value, cancellationToken);
        await using var _ = archiveFile;

        var managedCollectionLoadoutGroup = ManagedCollectionLoadoutGroup.Load(connection.Db, groupId);
        var collection = managedCollectionLoadoutGroup.Collection;
        var (revisionNumber, revisionId) = await nexusModsLibrary.UploadDraftRevision(collection, streamFactory, collectionManifest, cancellationToken);

        using var tx = connection.BeginTransaction();
        tx.Add(groupId, ManagedCollectionLoadoutGroup.CurrentRevisionNumber, revisionNumber);
        tx.Add(groupId, ManagedCollectionLoadoutGroup.CurrentRevisionId, revisionId);
        tx.Add(groupId, ManagedCollectionLoadoutGroup.LastUploadDate, DateTimeOffset.UtcNow);

        await tx.Commit();

        return (collection, revisionId, revisionNumber);
    }

    private static async ValueTask<IStreamFactory> CreateArchive(
        AbsolutePath archiveFilePath,
        JsonSerializerOptions jsonSerializerOptions,
        CollectionRoot collectionManifest,
        CancellationToken cancellationToken)
    {
        await using (var archiveStream = archiveFilePath.Open(mode: FileMode.Create, access: FileAccess.ReadWrite, share: FileShare.Read))
        using (var zipArchive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8))
        {
            var entry = zipArchive.CreateEntry("collection.json");
            await using var entryStream = entry.Open();
            await JsonSerializer.SerializeAsync(entryStream, collectionManifest, jsonSerializerOptions, cancellationToken: cancellationToken);
        }

        return new NativeFileStreamFactory(archiveFilePath);
    }

    /// <summary>
    /// Creates a collection JSON manifest from a loadout item group.
    /// </summary>
    public static CollectionRoot LoadoutItemGroupToCollectionManifest(
        LoadoutItemGroup.ReadOnly group,
        IGameDomainToGameIdMappingCache mappingCache,
        User.ReadOnly author,
        VanityVersion vanityVersion) => BuildManifest(group, mappingCache, author, vanityVersion).Manifest;

    /// <param name="Manifest">The collection to write out.</param>
    /// <param name="Skipped">Children of the group a manifest cannot name, so they are in neither <c>mods</c> nor the order.</param>
    public sealed record SharedManifest(CollectionRoot Manifest, IReadOnlyList<LoadoutItemGroup.ReadOnly> Skipped);

    /// <summary>
    /// <see cref="LoadoutItemGroupToCollectionManifest"/> plus the children it had to leave out. Besides <c>mods</c> it writes the
    /// group's order (<c>silverLoadOrder</c>, winner first, absent when no child can be named), the loadout's stored rules between
    /// the group's children (<c>modRules</c>) and the game's own Vortex order key when it has one.
    /// </summary>
    public static SharedManifest BuildManifest(
        LoadoutItemGroup.ReadOnly group,
        IGameDomainToGameIdMappingCache mappingCache,
        User.ReadOnly author,
        VanityVersion vanityVersion)
    {
        Debug.Assert(group.IsValid());

        var loadout = group.AsLoadoutItem().Loadout;
        var installation = loadout.Installation;
        if (!Sdk.Games.GameInstallMetadata.GameId.TryGetValue(installation, out var gameId))
            throw new InvalidOperationException($"{installation.Name} has no Nexus Mods id, so a Nexus collection cannot be made for it");
        var gameDomain = mappingCache[gameId];

        var priorities = LoadoutItemGroupPriority.FindByLoadout(group.Db, loadout.LoadoutId)
            .ToDictionary(row => row.TargetId, row => row.Priority.Value);

        // Winner first; a child with no priority row sorts last, in a stable order.
        var children = group.Children
            .OfTypeLoadoutItemGroup()
            .OfTypeLibraryLinkedLoadoutItem()
            .OrderByDescending(child => priorities.TryGetValue(child.AsLoadoutItemGroup().LoadoutItemGroupId, out var priority) ? (long)priority : -1L)
            .ThenBy(child => child.Id.Value)
            .ToArray();

        var shared = new List<LibraryLinkedLoadoutItem.ReadOnly>();
        var skipped = new List<LoadoutItemGroup.ReadOnly>();
        foreach (var child in children)
        {
            if (ModOrderMatcher.CanIdentify(child)) shared.Add(child);
            else skipped.Add(child.AsLoadoutItemGroup());
        }

        var tags = shared.ToDictionary(child => child.AsLoadoutItemGroup().LoadoutItemGroupId, child => child.Id.ToString());
        var collectionMods = new List<CollectionMod>();
        foreach (var child in shared)
        {
            var libraryItem = child.LibraryItem;
            var tag = tags[child.AsLoadoutItemGroup().LoadoutItemGroupId];
            var libraryFile = Optional<LibraryFile.ReadOnly>.None;
            if (libraryItem.TryGetAsDownloadedFile(out var downloadedFile)) libraryFile = downloadedFile.AsLibraryFile();

            collectionMods.Add(libraryItem.TryGetAsNexusModsLibraryItem(out var nexusModsLibraryItem)
                ? ToCollectionMod(nexusModsLibraryItem, libraryFile, tag)
                : ToCollectionMod(gameDomain, downloadedFile, tag));
        }

        var manifest = new CollectionRoot
        {
            Mods = collectionMods.ToArray(),
            ModRules = StoredRulesBetween(group.Db, loadout.LoadoutId, shared, tags),
            SilverLoadOrder = SilverOrderOf(shared),
            Info = new CollectionInfo
            {
                Name = group.AsLoadoutItem().Name,
                DomainName = gameDomain,
                Author = author.Name,
                Description = string.Empty,
                GameVersions = [vanityVersion.Value],
            },
        };

        var ordered = shared.Select(child => child.AsLoadoutItemGroup()).ToArray();
        manifest = WithVortexOrder(manifest, loadout.InstallationInstance.GetGame(), loadout, ordered);
        return new SharedManifest(manifest, skipped);
    }

    /// <summary>
    /// <paramref name="root"/> with the game's Vortex order key set from <paramref name="winnerToLoser"/>, when the game has a
    /// <see cref="IVortexOrderFormat"/> and it has something to write. A format whose key the app does not know throws.
    /// </summary>
    public static CollectionRoot WithVortexOrder(
        CollectionRoot root,
        IGame game,
        Loadout.ReadOnly loadout,
        IReadOnlyList<LoadoutItemGroup.ReadOnly> winnerToLoser)
    {
        if (game is not IModOrderGame { VortexOrderFormat: { } format }) return root;
        return format.Write(loadout, winnerToLoser) is { } value ? VortexOrderKeys.With(root, format.Key, value) : root;
    }

    private static SilverLoadOrderEntry[]? SilverOrderOf(IReadOnlyList<LibraryLinkedLoadoutItem.ReadOnly> winnerToLoser)
    {
        // Left out rather than written empty: an empty array counts as present on install and would hide a Vortex key.
        if (winnerToLoser.Count == 0) return null;

        return winnerToLoser.Select(child =>
        {
            var reference = ModOrderMatcher.ReferenceOf(child.AsLoadoutItemGroup());
            return new SilverLoadOrderEntry
            {
                Md5 = reference.Md5,
                ModId = reference.ModId,
                FileId = reference.FileId,
                Name = reference.Name ?? string.Empty,
                Enabled = !child.AsLoadoutItemGroup().AsLoadoutItem().IsDisabled,
            };
        }).ToArray();
    }

    /// <summary>
    /// Every stored rule whose two groups are both in <paramref name="shared"/>, referencing each group by its tag (the only thing a
    /// mod with no local file offers the install side to match on) and by md5 when one is known.
    /// </summary>
    private static ModRule[] StoredRulesBetween(
        IDb db,
        LoadoutId loadout,
        IReadOnlyList<LibraryLinkedLoadoutItem.ReadOnly> shared,
        IReadOnlyDictionary<LoadoutItemGroupId, string> tags)
    {
        var byId = shared.ToDictionary(child => child.AsLoadoutItemGroup().LoadoutItemGroupId);

        VortexModReference Reference(LoadoutItemGroupId id) => new()
        {
            Tag = tags[id],
            FileMD5 = ModOrderMatcher.ReferenceOf(byId[id].AsLoadoutItemGroup()).Md5,
        };

        return LoadoutGroupRule.FindByLoadout(db, loadout)
            .Where(rule => byId.ContainsKey(rule.SourceId) && byId.ContainsKey(rule.OtherId))
            .Select(rule => new ModRule
            {
                Type = rule.Kind == GroupRuleKind.After ? VortexModRuleType.After : VortexModRuleType.Before,
                Source = Reference(rule.SourceId),
                Other = Reference(rule.OtherId),
            })
            .ToArray();
    }

    private static CollectionMod ToCollectionMod(
        GameDomain gameDomain,
        DownloadedFile.ReadOnly downloadedFile,
        string tag)
    {
        var libraryFile = downloadedFile.AsLibraryFile();

        return new CollectionMod
        {
            Name = downloadedFile.AsLibraryFile().FileName,
            DomainName = gameDomain,
            Source = new ModSource
            {
                Type = ModSourceType.Browse,
                Md5 = libraryFile.Md5.Value,
                FileSize = libraryFile.Size,
                Url = downloadedFile.DownloadPageUri,
                Tag = tag,
            },
        };
    }

    private static CollectionMod ToCollectionMod(
        NexusModsLibraryItem.ReadOnly nexusModsLibraryItem,
        Optional<LibraryFile.ReadOnly> libraryFile,
        string tag)
    {
        var nexusModsFile = nexusModsLibraryItem.FileMetadata;
        var nexusModsModPage = nexusModsFile.ModPage;

        return new CollectionMod
        {
            Name = nexusModsFile.Name,
            Version = nexusModsFile.Version,
            DomainName = nexusModsModPage.GameDomain,
            Source = new ModSource
            {
                Type = ModSourceType.NexusMods,
                ModId = nexusModsModPage.Uid.ModId,
                FileId = nexusModsFile.Uid.FileId,
                UpdatePolicy = UpdatePolicy.ExactVersionOnly,
                FileSize = libraryFile.Convert(x => x.Size).ValueOr(() => Size.Zero),
                Md5 = libraryFile.Convert(x => x.Md5).ValueOr(() => Md5Value.From(0)),
                Tag = tag,
            },
        };
    }
}
