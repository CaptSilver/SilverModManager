using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft.Me3;
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

    private static readonly string[] MultiPartExtensions = [".tar.gz", ".tar.xz", ".tar.zst", ".tar.bz2"];

    /// <summary>The id a mod gets: the archive's name without its extension. Empty when nothing is left of the name.</summary>
    public static string ModId(LibraryArchive.ReadOnly archive)
        => ModId(archive.AsLibraryFile().AsLibraryItem().Name);

    /// <summary>
    /// Drops the whole archive extension, so <c>x.tar.gz</c> is <c>x</c> and not <c>x.tar</c>. A name that is only an
    /// extension (<c>.zip</c>) gives an empty id.
    /// </summary>
    public static string ModId(string archiveName)
    {
        foreach (var extension in MultiPartExtensions)
        {
            if (archiveName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return archiveName[..^extension.Length];
        }

        return Path.GetFileNameWithoutExtension(archiveName);
    }

    /// <summary>The reason shown when the archive's name leaves no id at all.</summary>
    public const string NoNameReason = "The archive has no name once its extension is removed, so there is nothing to call the mod. Rename the archive and add it again.";

    /// <summary>The reason shown when the archive's name leaves no usable package id, or null when <paramref name="id"/> is fine.</summary>
    public static string? IdRefusal(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return NoNameReason;

        return Me3ModsLayout.IsReservedPackageId(id)
            ? $"'{id}' is a reserved folder name in the mods folder, so a package cannot be called that. Rename the archive and add it again."
            : null;
    }

    /// <summary>
    /// Declines the archive and logs why: a declined archive falls through to the Advanced installer without a word,
    /// so the log is the only place the reason survives.
    /// </summary>
    public static ValueTask<InstallerResult> Decline(ILogger logger, string reason)
    {
        logger.LogInformation("The installer declined the archive: {Reason}", reason);
        return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: reason));
    }

    public static ArchiveFile[] Files(LibraryArchive.ReadOnly archive)
    {
        var tree = LibraryArchiveTreeExtensions.GetTree(archive);
        return tree.Item.GetFiles<LibraryArchiveTree, RelativePath>()
            .Select(f => new ArchiveFile(f.Item.Path.Path, f.Item.LibraryFile.Value.Hash, f.Item.LibraryFile.Value.Size))
            .ToArray();
    }

    /// <summary>Adds <paramref name="file"/> to the group, to be deployed at <paramref name="pathInMods"/> inside the mods folder.</summary>
    public static void AddFile(ITransaction tx, Loadout.ReadOnly loadout, LoadoutItemGroup.New group, ArchiveFile file, string pathInMods, string name)
        => AddFile(tx, loadout, group, file, LocationId.Me3Mods, pathInMods, name);

    /// <summary>Adds <paramref name="file"/> to the group, to be deployed at <paramref name="path"/> inside <paramref name="location"/>.</summary>
    public static void AddFile(ITransaction tx, Loadout.ReadOnly loadout, LoadoutItemGroup.New group, ArchiveFile file, LocationId location, string path, string name)
    {
        _ = new LoadoutFile.New(tx, out var id)
        {
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
            {
                TargetPath = (loadout.Id, location, (RelativePath)path),
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
