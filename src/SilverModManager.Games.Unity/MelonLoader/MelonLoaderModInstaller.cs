using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths.Trees.Traits;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Unity.MelonLoader;

/// <summary>
/// Installs a MelonLoader mod: each file goes where <see cref="MelonLoaderLayout"/> routes it. The loader pack is left
/// to <see cref="MelonLoaderPackInstaller"/>, which the loader lists first; this installer refuses one so a pack that
/// slipped past it is not scattered across the mod folders.
/// </summary>
public sealed class MelonLoaderModInstaller(IServiceProvider serviceProvider, MelonLoaderLoader loader)
    : ALibraryArchiveInstaller(serviceProvider, serviceProvider.GetRequiredService<ILogger<MelonLoaderModInstaller>>())
{
    private readonly ILogger<MelonLoaderModInstaller> _logger = serviceProvider.GetRequiredService<ILogger<MelonLoaderModInstaller>>();

    public override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction tx,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        var files = LibraryArchiveTreeExtensions.GetTree(libraryArchive).GetFiles().ToArray();
        if (files.Length == 0)
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: "The archive has no files."));

        var paths = files.Select(f => f.Item.Path).ToArray();
        var itemName = libraryArchive.AsLibraryFile().AsLibraryItem().Name;

        if (MelonLoaderLayout.IsLoaderPack(paths, out _) || loader.IsLoaderPackName(itemName))
        {
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason:
                $"The archive is the {loader.LoaderPackage} loader pack, which the loader pack installer installs, not a mod."));
        }

        IReadOnlyDictionary<NexusMods.Paths.RelativePath, Sdk.Games.GamePath?> routes;
        try
        {
            routes = MelonLoaderLayout.Route(paths, UnityRuntimeBranch.Of(loadout.InstallationInstance, loader.Definition));
        }
        catch (MelonLoaderLayoutException e)
        {
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: e.Message));
        }

        if (routes.Values.All(target => target is null))
        {
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason:
                "The archive has nothing MelonLoader loads: no .dll, and no .cfg, .json or .ini settings file."));
        }

        foreach (var file in files)
        {
            if (routes[file.Item.Path] is { } target)
                file.ToLoadoutFile(loadout.Id, loadoutGroup.Id, tx, target);
            else if (file.Item.Path.Extension.ToString().Equals(".dll", StringComparison.OrdinalIgnoreCase))
                _logger.LogInformation("Not installing {Path} from {Archive}: it is built for the other scripting backend", file.Item.Path, itemName);
        }

        return ValueTask.FromResult<InstallerResult>(new Success());
    }
}
