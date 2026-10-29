using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Abstractions.Library;
using SilverModManager.Collections;
using SilverModManager.Games.FOMOD;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.NexusModsApi;

using ModSource = SilverModManager.Abstractions.Collections.Json.ModSource;

namespace SilverModManager.Games.TestFramework.Helpers;

using CollectionResult = (NexusModsCollectionLibraryFile.ReadOnly File, CollectionRevisionMetadata.ReadOnly Revision, CollectionDownload.ReadOnly[] Items);

/// <summary>
/// Builds a collection that is already in the database: the collection.json is written as a library file, the downloads are
/// entities, and nothing touches the network. The mods are zips on disk, or (from a manifest) files the library already holds.
/// </summary>
public sealed class OfflineCollections(IServiceProvider serviceProvider, IConnection connection, ILibraryService libraryService, TemporaryFileManager temporaryFileManager)
{
    private const string ModuleConfig = """
        <config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="http://qconsulting.ca/fo3/ModConfig5.0.xsd">
            <moduleName>Mod {0}</moduleName>
            <installSteps order="Explicit">
                <installStep name="Step 1">
                    <optionalFileGroups>
                        <group name="Group 1" type="SelectExactlyOne">
                            <plugins order="Explicit">
                                <plugin name="Option A">
                                    <description>A</description>
                                    <files><file source="mod{0}_a.esp" destination="mod{0}_a.out.esp"/></files>
                                    <typeDescriptor><type name="Optional"/></typeDescriptor>
                                </plugin>
                                <plugin name="Option B">
                                    <description>B</description>
                                    <files><file source="mod{0}_b.esp" destination="mod{0}_b.out.esp"/></files>
                                    <typeDescriptor><type name="Optional"/></typeDescriptor>
                                </plugin>
                            </plugins>
                        </group>
                    </optionalFileGroups>
                </installStep>
                <installStep name="Step 2">
                    <optionalFileGroups>
                        <group name="Group 1" type="SelectAny">
                            <plugins order="Explicit">
                                <plugin name="Option C">
                                    <description>C</description>
                                    <files><file source="mod{0}_c.esp" destination="mod{0}_c.out.esp"/></files>
                                    <typeDescriptor><type name="Optional"/></typeDescriptor>
                                </plugin>
                            </plugins>
                        </group>
                    </optionalFileGroups>
                </installStep>
            </installSteps>
        </config>
        """;

    public async Task<(AbsolutePath Path, Md5Value Md5)> CreateFomodMod(AbsolutePath folder, int index, bool plain)
    {
        var path = folder.Combine($"mod{index}.zip");
        await using (var stream = path.Create())
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }

            if (plain)
            {
                Add($"archive/pc/mod/plain{index}.archive", $"plain mod {index}");
                Add($"mods/plain{index}/info.json", $$"""{ "name": "plain{{index}}", "version": "1.0.0" }""");
            }
            else
            {
                Add("fomod/ModuleConfig.xml", string.Format(ModuleConfig, index));
                foreach (var option in "abc") Add($"mod{index}_{option}.esp", $"mod {index} option {option}");
            }
        }

        await using var read = path.Read();
        return (path, await Md5Hasher.HashAsync(read));
    }


    /// <summary>The collection of the given FOMOD or plain mods; <paramref name="rules"/> are written as stored download rules directly.</summary>
    public async Task<CollectionResult> Create(
        (FomodOption[]? Choices, string Name)[] fomodMods, int[]? optionalIndexes = null,
        (int Source, int Other, CollectionDownloadRuleType Type)[]? rules = null,
        Func<IReadOnlyList<Mod>, SilverLoadOrderEntry[]>? silverLoadOrder = null)
    {
        var folder = temporaryFileManager.CreateFolder();

        var mods = new List<Mod>();
        var archives = new List<(AbsolutePath Path, Md5Value Md5)>();
        for (var i = 0; i < fomodMods.Length; i++)
        {
            var archive = await CreateFomodMod(folder.Path, i, plain: fomodMods[i].Choices is null);
            archives.Add(archive);
            mods.Add(new Mod
            {
                Name = fomodMods[i].Name,
                DomainName = GameDomain.From("cyberpunk2077"),
                Source = new ModSource
                {
                    Type = ModSourceType.Direct,
                    Md5 = archive.Md5,
                    Url = new Uri($"https://example.invalid/mod{i}.zip"),
                    LogicalFilename = $"mod{i}.zip",
                    FileSize = Size.From((ulong)archive.Path.FileInfo.Size),
                },
                Choices = fomodMods[i].Choices is { } choices ? new Choices { Type = ChoicesType.fomod, Options = choices } : null,
            });
        }

        var root = new CollectionRoot
        {
            Info = new CollectionInfo { Name = "Offline", DomainName = GameDomain.From("cyberpunk2077") },
            Mods = mods.ToArray(),
            SilverLoadOrder = silverLoadOrder?.Invoke(mods),
        };

        var (fileId, revisionId) = await Persist(folder.Path, root, optionalIndexes, (tx, downloads) =>
        {
            foreach (var (source, other, type) in rules ?? [])
            {
                _ = new CollectionDownloadRules.New(tx)
                {
                    SourceId = downloads[source],
                    OtherId = downloads[other],
                    RuleType = type,
                    ArrayIndex = 0,
                };
            }
        });

        // The mod archives go into the library as the direct downloads the collection refers to.
        foreach (var (path, md5) in archives) await AddDirectDownload(path, md5);

        return Load(fileId, revisionId);
    }

    /// <summary>
    /// The collection a manifest describes, as the online fetch would store it: <c>mods[]</c> become the downloads and the
    /// <c>modRules</c> are matched to them by <see cref="NexusModsLibrary.AddCollectionDownloadRules"/>. The manifest goes
    /// through the JSON serializer first, so the rules are matched from what a family member's copy would carry. The mods'
    /// files must already be in the library (see <see cref="AddDirectDownload"/>).
    /// </summary>
    /// <param name="bundledFiles">The files of the <c>bundle</c> mods, by the mod's file expression; they go under <c>bundled/&lt;expression&gt;/</c> in the collection archive.</param>
    /// <param name="slug">The collection's slug; revisions with the same slug belong to the same collection.</param>
    public async Task<CollectionResult> Create(CollectionRoot manifest, int[]? optionalIndexes = null, int revisionNumber = 1, IReadOnlyDictionary<string, (string Path, string Content)[]>? bundledFiles = null, string slug = "offline")
    {
        var options = serviceProvider.GetRequiredService<JsonSerializerOptions>();
        var folder = temporaryFileManager.CreateFolder();
        var parsed = JsonSerializer.Deserialize<CollectionRoot>(JsonSerializer.Serialize(manifest, options), options)!;

        var (fileId, revisionId) = await Persist(folder.Path, parsed, optionalIndexes, addRules: null, revisionNumber, bundledFiles, slug);

        // The online fetch commits the revision first and matches the rules against what it stored.
        using var ruleTx = connection.BeginTransaction();
        NexusModsLibrary.AddCollectionDownloadRules(ruleTx, parsed, CollectionRevisionMetadata.Load(connection.Db, revisionId));
        await ruleTx.Commit();

        return Load(fileId, revisionId);
    }

    /// <summary>Puts a zip in the library the way an external collection download lands there: with its md5 and a logical file name.</summary>
    public async Task AddDirectDownload(AbsolutePath path, Md5Value md5)
    {
        var file = await libraryService.AddLocalFile(path);
        using var fileTx = connection.BeginTransaction();
        fileTx.Add(file.Id, LibraryFile.Md5, md5);
        fileTx.Add(file.Id, DirectDownloadLibraryFile.LogicalFileName, path.FileName);
        await fileTx.Commit();
    }

    /// <summary>The game id every offline collection and Nexus file uses.</summary>
    public static readonly NexusModsGameId Game = NexusModsGameId.From(3333);

    /// <summary>
    /// Puts a zip in the library the way a Nexus download lands there: a library file that is also a
    /// <see cref="NexusModsLibraryItem"/> pointing at the file's metadata, which a collection download with the same uid shares.
    /// </summary>
    public async Task AddNexusDownload(AbsolutePath path, uint modId, uint fileId)
    {
        var file = await libraryService.AddLocalFile(path);
        using var tx = connection.BeginTransaction();
        var (page, metadata) = NexusMetadata(tx, modId, fileId, path.FileName);
        tx.Add(file.Id, NexusModsLibraryItem.FileMetadata, metadata);
        tx.Add(file.Id, NexusModsLibraryItem.ModPageMetadata, page);
        await tx.Commit();
    }

    /// <summary>The mod page and file metadata for the uids, made in <paramref name="tx"/> unless the database already has them.</summary>
    private (EntityId Page, EntityId File) NexusMetadata(ITransaction tx, uint modId, uint fileId, string name)
    {
        var db = connection.Db;
        var modUid = new ModUid(ModId.From(modId), Game);
        var fileUid = new FileUid(FileId.From(fileId), Game);

        var page = NexusModsModPageMetadata.FindByUid(db, modUid).Select(static p => (EntityId?)p.Id).FirstOrDefault()
            ?? new NexusModsModPageMetadata.New(tx)
            {
                Uid = modUid,
                Name = name,
                GameDomain = GameDomain.From("cyberpunk2077"),
                UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            }.Id;

        var file = NexusModsFileMetadata.FindByUid(db, fileUid).Select(static f => (EntityId?)f.Id).FirstOrDefault()
            ?? new NexusModsFileMetadata.New(tx)
            {
                Uid = fileUid,
                Name = name,
                Version = fileId.ToString(),
                UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                ModPageId = page,
            }.Id;

        return (page, file);
    }

    private CollectionResult Load(EntityId fileId, EntityId revisionId)
    {
        var db = connection.Db;
        var revision = CollectionRevisionMetadata.Load(db, revisionId);
        return (NexusModsCollectionLibraryFile.Load(db, fileId), revision, CollectionDownloader.GetItems(revision, CollectionDownloader.ItemType.Required));
    }

    /// <summary>Writes the collection.json, its library file, the revision and one download per <c>mods[]</c> entry; <paramref name="addRules"/> runs in the same transaction.</summary>
    private async Task<(EntityId File, EntityId Revision)> Persist(
        AbsolutePath folder, CollectionRoot root, int[]? optionalIndexes,
        Action<ITransaction, List<CollectionDownload.New>>? addRules,
        int revisionNo = 1,
        IReadOnlyDictionary<string, (string Path, string Content)[]>? bundledFiles = null,
        string slugText = "offline")
    {
        var slug = CollectionSlug.From(slugText);
        var revisionNumber = RevisionNumber.From((ulong)revisionNo);

        var collectionJson = folder.Combine("collection.json");
        var collectionZip = folder.Combine("collection.zip");
        await collectionJson.WriteAllTextAsync(JsonSerializer.Serialize(root, serviceProvider.GetRequiredService<JsonSerializerOptions>()));
        await using (var stream = collectionZip.Create())
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(collectionJson.ToString(), "collection.json");
            foreach (var (expression, files) in bundledFiles ?? new Dictionary<string, (string Path, string Content)[]>())
            {
                foreach (var (path, content) in files)
                {
                    using var writer = new StreamWriter(zip.CreateEntry($"bundled/{expression}/{path}").Open());
                    writer.Write(content);
                }
            }
        }

        var collectionLibraryFile = await libraryService.AddLocalFile(collectionZip);

        using var tx = connection.BeginTransaction();

        tx.Add(collectionLibraryFile.Id, NexusModsCollectionLibraryFile.CollectionSlug, slug);
        tx.Add(collectionLibraryFile.Id, NexusModsCollectionLibraryFile.CollectionRevisionNumber, revisionNumber);

        // A later revision belongs to the collection the first one created.
        var existing = CollectionMetadata.All(connection.Db).FirstOrDefault(c => c.Slug == slug);
        EntityId collectionEntity;
        if (existing.IsValid())
        {
            collectionEntity = existing.Id;
        }
        else
        {
            var author = new User.New(tx) { Name = "Author", NexusId = (ulong)CollectionMetadata.All(connection.Db).Count() + 1 };
            collectionEntity = new CollectionMetadata.New(tx)
            {
                CollectionId = CollectionId.From((ulong)CollectionMetadata.All(connection.Db).Count() + 1),
                Slug = slug,
                Name = slugText == "offline" ? "Offline" : slugText,
                GameId = Game,
                AuthorId = author,
            }.Id;
        }

        var revision = new CollectionRevisionMetadata.New(tx)
        {
            // Revision ids are unique across collections; the default collection keeps its number as its id.
            RevisionId = RevisionId.From(slugText == "offline" ? (ulong)revisionNo : 1_000_000UL + (ulong)CollectionRevisionMetadata.All(connection.Db).Count()),
            RevisionNumber = revisionNumber,
            CollectionId = collectionEntity,
        };

        var downloads = new List<CollectionDownload.New>();
        for (var i = 0; i < root.Mods.Length; i++)
        {
            var mod = root.Mods[i];
            var download = new CollectionDownload.New(tx)
            {
                ArrayIndex = i,
                CollectionRevisionId = revision,
                IsOptional = optionalIndexes?.Contains(i) ?? false,
                Name = mod.Name,
            };
            downloads.Add(download);

            if (mod.Source.Type == ModSourceType.Bundle)
            {
                _ = new CollectionDownloadBundled.New(tx, download.Id)
                {
                    CollectionDownload = download,
                    BundledPath = mod.Source.FileExpression,
                };
                continue;
            }

            if (mod.Source.Type == ModSourceType.NexusMods)
            {
                // Same as the online fetch: the download shares the file's metadata entity with the library item.
                var (_, metadata) = NexusMetadata(tx, mod.Source.ModId.Value, mod.Source.FileId.Value, mod.Name);
                _ = new CollectionDownloadNexusMods.New(tx, download.Id)
                {
                    CollectionDownload = download,
                    ModUid = new ModUid(mod.Source.ModId, Game),
                    FileUid = new FileUid(mod.Source.FileId, Game),
                    FileMetadataId = metadata,
                };
                continue;
            }

            _ = new CollectionDownloadExternal.New(tx, download.Id)
            {
                CollectionDownload = download,
                Md5 = mod.Source.Md5,
                Size = mod.Source.FileSize,
                Uri = mod.Source.Url ?? new Uri("https://example.invalid/mod.zip"),
            };
        }

        addRules?.Invoke(tx, downloads);

        var committed = await tx.Commit();
        return (collectionLibraryFile.Id, committed[revision.Id]);
    }
}
