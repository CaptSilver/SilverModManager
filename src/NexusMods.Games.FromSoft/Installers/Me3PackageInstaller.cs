using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Paths.Trees.Traits;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.FromSoft.Installers;

/// <summary>
/// Installs a mod archive as a me3 package: the folder that mirrors the game's archive layout, placed under
/// <see cref="NexusMods.Sdk.Games.LocationId.Me3Mods"/> in a folder named after the archive. Nothing is ever installed into
/// the game folder.
/// </summary>
public class Me3PackageInstaller : ALibraryArchiveInstaller
{
    public Me3PackageInstaller(IServiceProvider serviceProvider)
        : base(serviceProvider, serviceProvider.GetRequiredService<ILogger<Me3PackageInstaller>>())
    {
    }

    public override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction tx,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        var tree = LibraryArchiveTreeExtensions.GetTree(libraryArchive);
        var files = tree.Item.GetFiles<LibraryArchiveTree, RelativePath>().ToArray();
        var paths = files.Select(f => f.Item.Path).ToArray();

        if (!Me3PackageLayout.TryFindRoot(paths, out var root))
        {
            var options = Me3PackageLayout.ListOptionDirs(paths);
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: options.Count > 0
                ? $"The archive holds the option folders {string.Join(", ", options.Select(o => $"'{o}'"))} and the installer cannot pick one. Install the one you want with the Advanced installer."
                : "The archive has none of the game's asset folders (parts, menu, msg, ...), so it is not a content package."));
        }

        var packageId = Path.GetFileNameWithoutExtension(libraryArchive.AsLibraryFile().AsLibraryItem().Name);
        var rootPrefix = root == RelativePath.Empty ? "" : root.Path + "/";

        foreach (var file in files)
        {
            var fullPath = file.Item.Path.Path;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var relativePath = fullPath[rootPrefix.Length..];
            _ = new LoadoutFile.New(tx, out var id)
            {
                LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
                {
                    TargetPath = (loadout.Id, LocationId.Me3Mods, (RelativePath)$"{packageId}/{relativePath}"),
                    LoadoutItem = new LoadoutItem.New(tx, id)
                    {
                        Name = relativePath,
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
