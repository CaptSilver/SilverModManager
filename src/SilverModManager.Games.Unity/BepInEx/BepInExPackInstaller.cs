using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Paths.Trees.Traits;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.Unity.BepInEx;

/// <summary>
/// Installs a BepInEx loader pack into the game folder. The pack's files sit under one root folder
/// (<c>BepInExPack/</c>, <c>BepInExPack_PEAK/</c>, ...) that is left off, so the doorstop proxy lands next to the executable.
/// The manifest, readme and icon every Thunderstore package carries are not installed. An archive is taken for a pack
/// when its files say so, or when its name is the game's loader package or one of its aliases; the name covers
/// BepInEx 6 packs, which have no BepInEx.Preloader.dll.
/// </summary>
public sealed class BepInExPackInstaller(IServiceProvider serviceProvider, BepInExLoader loader)
    : ALibraryArchiveInstaller(serviceProvider, serviceProvider.GetRequiredService<ILogger<BepInExPackInstaller>>())
{
    // Compared lower-cased, as r2modman's BepInEx installer does.
    private static readonly string[] RootMetadata = ["manifest.json", "readme.md", "icon.png", "changelog.md"];

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
                $"The archive is not the {loader.LoaderPackage} loader pack: it has no folder with {BepInExFiles.DoorstopProxy}, " +
                $"{BepInExFiles.DoorstopConfig} and {BepInExFiles.Root}/{BepInExFiles.Core}/{BepInExFiles.Preloader}, " +
                $"and its name is not {loader.LoaderPackage} or one of its aliases."));
        }

        var prefix = root.Length == 0 ? "" : root + "/";
        foreach (var file in files)
        {
            var path = file.Item.Path.ToString();
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var relative = path[prefix.Length..];
            if (!relative.Contains('/') && RootMetadata.Contains(relative.ToLowerInvariant())) continue;

            file.ToLoadoutFile(loadout, loadoutGroup, tx, new GamePath(LocationId.Game, relative));
        }

        return ValueTask.FromResult<InstallerResult>(new Success());
    }

    private static bool TryFindRoot(RelativePath[] paths, bool nameMatches, out string root)
    {
        if (BepInExLayout.IsLoaderPack(paths, out root)) return true;
        if (!nameMatches) return false;

        // The name vouches for the archive, so the proxy alone locates the root; at most one folder deep, as for the
        // content check.
        foreach (var path in paths)
        {
            var text = path.ToString();
            if (!Path.GetFileName(text).Equals(BepInExFiles.DoorstopProxy, StringComparison.OrdinalIgnoreCase)) continue;

            var directory = text[..^BepInExFiles.DoorstopProxy.Length].TrimEnd('/');
            if (directory.Contains('/')) continue;

            root = directory;
            return true;
        }

        root = "";
        return false;
    }
}
