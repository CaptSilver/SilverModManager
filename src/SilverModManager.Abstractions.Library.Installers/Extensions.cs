using DynamicData.Kernel;
using JetBrains.Annotations;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Paths.Trees;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Library.Installers;

[PublicAPI]
public static class Extensions
{
    /// <summary>
    /// Convert as LibraryArchiveTree node to a LoadoutFile with the given metadata
    /// </summary>
    public static LoadoutFile.New ToLoadoutFile(
        this KeyedBox<RelativePath, LibraryArchiveTree> input,
        LoadoutId loadoutId,
        LoadoutItemGroupId parent,
        ITransaction tx,
        GamePath to,
        Optional<EntityId> entityId = default)
    {
        var id = entityId.ValueOr(tx.TempId);
        var libraryFile = input.Item.LibraryFile.Value;

        return new LoadoutFile.New(tx, id)
        {
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
            {
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    LoadoutId = loadoutId,
                    Name = input.Item.Value.FileName,
                    ParentId = parent,
                },
                TargetPath = to.ToGamePathParentTuple(loadoutId),
            },
            Hash = libraryFile.Hash,
            Size = libraryFile.Size,
        };
    }
}
