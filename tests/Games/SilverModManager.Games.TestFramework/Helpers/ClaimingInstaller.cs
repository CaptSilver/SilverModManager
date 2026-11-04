using NexusMods.Paths;
using NexusMods.Paths.Trees.Traits;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.TestFramework.Helpers;

/// <summary>
/// An installer that knows the layout of the named archives and nothing else: it writes each of their files to
/// <c>Game/&lt;path in the archive&gt;</c> and declines every other archive.
/// </summary>
/// <param name="archiveNames">The library item names it claims, such as <c>mod1.zip</c>.</param>
public sealed class ClaimingInstaller(params string[] archiveNames) : ILibraryItemInstaller
{
    /// <inheritdoc/>
    public bool IsSupportedLibraryItem(LibraryItem.ReadOnly libraryItem) => archiveNames.Contains(libraryItem.Name);

    /// <inheritdoc/>
    public ValueTask<InstallerResult> ExecuteAsync(
        LibraryItem.ReadOnly libraryItem, LoadoutItemGroup.New loadoutGroup, ITransaction transaction,
        Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        if (!IsSupportedLibraryItem(libraryItem)
            || !libraryItem.TryGetAsLibraryFile(out var libraryFile)
            || !libraryFile.TryGetAsLibraryArchive(out var libraryArchive))
            return ValueTask.FromResult<InstallerResult>(new NotSupported($"{libraryItem.Name} is not an archive this installer claims"));

        var tree = LibraryArchiveTreeExtensions.GetTree(libraryArchive);
        foreach (var file in tree.Item.GetFiles<LibraryArchiveTree, RelativePath>())
        {
            var path = file.Item.Path;
            _ = new LoadoutFile.New(transaction, out var id)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(transaction, id)
                {
                    TargetPath = (loadout.Id, LocationId.Game, path),
                    LoadoutItem = new LoadoutItem.New(transaction, id)
                    {
                        Name = path.Name,
                        LoadoutId = loadout.Id,
                        ParentId = loadoutGroup.Id,
                    },
                },
                Hash = file.Item.LibraryFile.Value.Hash,
                Size = file.Item.LibraryFile.Value.Size,
            };
        }

        return ValueTask.FromResult<InstallerResult>(new Success());
    }
}
