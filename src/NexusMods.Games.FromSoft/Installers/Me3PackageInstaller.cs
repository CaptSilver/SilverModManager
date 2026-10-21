using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
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
        var files = Me3InstallerFiles.Files(libraryArchive);
        var paths = files.Select(f => (RelativePath)f.Path).ToArray();

        if (!Me3PackageLayout.TryFindRoot(paths, out var root))
        {
            var options = Me3PackageLayout.ListOptionDirs(paths);
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: options.Count > 0
                ? $"The archive holds the option folders {string.Join(", ", options.Select(o => $"'{o}'"))} and the installer cannot pick one. Install the one you want with the Advanced installer."
                : "The archive has none of the game's asset folders (parts, menu, msg, ...), so it is not a content package."));
        }

        var packageId = Me3InstallerFiles.ModId(libraryArchive);
        if (Me3ModsLayout.IsReservedPackageId(packageId))
        {
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason:
                $"'{packageId}' is a reserved folder name in the mods folder, so a package cannot be called that. Rename the archive and add it again."));
        }

        var rootPrefix = root == RelativePath.Empty ? "" : root.Path + "/";

        foreach (var file in files)
        {
            if (!file.Path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var relativePath = file.Path[rootPrefix.Length..];
            Me3InstallerFiles.AddFile(tx, loadout, loadoutGroup, file, $"{packageId}/{relativePath}", relativePath);
        }

        return ValueTask.FromResult<InstallerResult>(new Success());
    }
}
