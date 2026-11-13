using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Paths.Trees.Traits;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Unity;

/// <summary>
/// Installs a loader pack into the game folder. The pack's files sit under at most one root folder, which is left off so
/// the proxy DLL lands next to the executable; the Thunderstore metadata at that root is not installed. An archive is
/// taken for the pack when its files say so (<see cref="IsLoaderPack"/>), or when its name is the game's loader package
/// or one of its aliases.
/// </summary>
public abstract class AUnityLoaderPackInstaller(IServiceProvider serviceProvider, ILogger logger, AUnityModLoader loader)
    : ALibraryArchiveInstaller(serviceProvider, logger)
{
    /// <summary>Whether the archive's files are the loader's pack, and the folder they sit under ("" for the archive root).</summary>
    protected abstract bool IsLoaderPack(RelativePath[] paths, out string root);

    /// <summary>What a pack holds, for the refusal of an archive that is not one, e.g. "a folder with version.dll".</summary>
    protected abstract string PackLayoutDescription { get; }

    public override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction tx,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        var files = LibraryArchiveTreeExtensions.GetTree(libraryArchive).GetFiles().ToArray();
        var paths = files.Select(f => f.Item.Path).ToArray();
        var itemName = libraryArchive.AsLibraryFile().AsLibraryItem().Name;

        if (!TryFindRoot(paths, loader.IsLoaderPackName(itemName), out var root))
        {
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason:
                $"The archive is not the {loader.LoaderPackage} loader pack: it has no {PackLayoutDescription}, " +
                $"and its name is not {loader.LoaderPackage} or one of its aliases."));
        }

        var prefix = root.Length == 0 ? "" : root + "/";
        foreach (var file in files)
        {
            var path = file.Item.Path.ToString();
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var relative = path[prefix.Length..];
            if (ThunderstorePackageMetadata.IsAtRoot(relative)) continue;

            file.ToLoadoutFile(loadout, loadoutGroup, tx, new GamePath(LocationId.Game, relative));
        }

        return ValueTask.FromResult<InstallerResult>(new Success());
    }

    private bool TryFindRoot(RelativePath[] paths, bool nameMatches, out string root)
    {
        if (IsLoaderPack(paths, out root)) return true;
        if (!nameMatches) return false;

        // The name vouches for the archive, so the proxy alone locates the root; at most one folder deep, as for the
        // content check.
        var proxy = loader.Proxy.FileName.ToString();
        foreach (var path in paths)
        {
            var text = path.ToString();
            if (!Path.GetFileName(text).Equals(proxy, StringComparison.OrdinalIgnoreCase)) continue;

            var directory = text[..^proxy.Length].TrimEnd('/');
            if (directory.Contains('/')) continue;

            root = directory;
            return true;
        }

        root = "";
        return false;
    }
}
