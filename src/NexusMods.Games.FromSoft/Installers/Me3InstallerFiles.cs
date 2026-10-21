using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Paths.Trees.Traits;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Installers;

/// <summary>What the installers share: reading an archive's files and turning one into a loadout file in the mods folder.</summary>
internal static class Me3InstallerFiles
{
    internal readonly record struct ArchiveFile(string Path, Hash Hash, Size Size);

    /// <summary>The id a mod gets: the archive's name without its extension.</summary>
    public static string ModId(LibraryArchive.ReadOnly archive)
        => Path.GetFileNameWithoutExtension(archive.AsLibraryFile().AsLibraryItem().Name);

    public static ArchiveFile[] Files(LibraryArchive.ReadOnly archive)
    {
        var tree = LibraryArchiveTreeExtensions.GetTree(archive);
        return tree.Item.GetFiles<LibraryArchiveTree, RelativePath>()
            .Select(f => new ArchiveFile(f.Item.Path.Path, f.Item.LibraryFile.Value.Hash, f.Item.LibraryFile.Value.Size))
            .ToArray();
    }

    /// <summary>Adds <paramref name="file"/> to the group, to be deployed at <paramref name="pathInMods"/> inside the mods folder.</summary>
    public static void AddFile(ITransaction tx, Loadout.ReadOnly loadout, LoadoutItemGroup.New group, ArchiveFile file, string pathInMods, string name)
    {
        _ = new LoadoutFile.New(tx, out var id)
        {
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
            {
                TargetPath = (loadout.Id, LocationId.Me3Mods, (RelativePath)pathInMods),
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    Name = name,
                    LoadoutId = loadout.Id,
                    ParentId = group.Id,
                },
            },
            Hash = file.Hash,
            Size = file.Size,
        };
    }
}
