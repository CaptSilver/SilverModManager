using System.IO.Compression;
using SilverModManager.Sdk.Hashes;
using System.Text;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

/// <summary>Archives and groups the Witcher 3 installer and emitter tests build their loadouts from.</summary>
internal static class Witcher3Fixtures
{
    /// <summary>Writes a zip at <paramref name="file"/> whose entries each hold their own path as content.</summary>
    public static async Task<AbsolutePath> WriteArchive(AbsolutePath file, params string[] paths)
    {
        await using var stream = file.Create();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var path in paths)
        {
            await using var entry = zip.CreateEntry(path).Open();
            await entry.WriteAsync(Encoding.UTF8.GetBytes(path));
        }

        return file;
    }

    /// <summary>
    /// A collection group the way the collection installer makes one, with nothing in it. Its references point at
    /// the loadout because nothing here reads the collection or its revision.
    /// </summary>
    public static async Task<LoadoutItemGroupId> CreateNexusCollectionGroup(IConnection connection, LoadoutId loadout, string name)
    {
        using var tx = connection.BeginTransaction();
        var group = new NexusCollectionLoadoutGroup.New(tx, out var id)
        {
            CollectionId = loadout.Value,
            RevisionId = loadout.Value,
            LibraryFileId = loadout.Value,
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
        return result.Remap(group).AsCollectionGroup().AsLoadoutItemGroup().LoadoutItemGroupId;
    }

    /// <summary>
    /// A mod group holding <paramref name="files"/> (a game path and the text it holds), ranked at
    /// <paramref name="priority"/>. The bytes are in the file store, so a bake can read them.
    /// </summary>
    public static async Task<LoadoutItemGroupId> AddMod(
        IConnection connection, IFileStore fileStore, LoadoutId loadout, string name, ulong priority, params (string Path, string Content)[] files)
    {
        var contents = files.Select(f => Encoding.UTF8.GetBytes(f.Content)).ToArray();
        await fileStore.BackupFiles(contents.Select(bytes =>
            new ArchivedFileEntry(new MemoryStreamFactory("file", new MemoryStream(bytes)), FileIdentity.XxHash3(bytes.xxHash3()), Size.FromLong(bytes.Length))).ToArray());

        using var tx = connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { LoadoutId = loadout, Name = name },
        };
        for (var i = 0; i < files.Length; i++)
        {
            var path = new GamePath(LocationId.Game, files[i].Path);
            _ = new LoadoutFile.New(tx, out var fileId)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, fileId)
                {
                    LoadoutItem = new LoadoutItem.New(tx, fileId) { LoadoutId = loadout, ParentId = group.Id, Name = path.Path },
                    TargetPath = path.ToGamePathParentTuple(loadout),
                },
                Hash = contents[i].xxHash3(),
                Size = Size.FromLong(contents[i].Length),
            };
        }
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout, TargetId = group.Id, Priority = ConflictPriority.From(priority) };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group.Id]);
    }
}
