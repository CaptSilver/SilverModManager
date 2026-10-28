using System.IO.Compression;
using System.Text;
using NexusMods.Paths;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
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
}
