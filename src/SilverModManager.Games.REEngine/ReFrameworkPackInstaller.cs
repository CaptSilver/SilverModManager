using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths.Trees.Traits;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.REEngine;

/// <summary>
/// Installs the REFramework loader. Its download is a zip holding dinput8.dll at the root or in one folder, and
/// REFramework's README says to extract only that file into the game folder; the VR files beside it are left out. An
/// archive that also holds files below a subfolder of <c>natives</c> or <c>reframework</c> is a mod that ships its own copy of the loader, and is
/// left to <see cref="ReEngineModInstaller"/>'s refusal rather than half installed here.
/// </summary>
public sealed class ReFrameworkPackInstaller(IServiceProvider serviceProvider)
    : ALibraryArchiveInstaller(serviceProvider, serviceProvider.GetRequiredService<ILogger<ReFrameworkPackInstaller>>())
{
    public override ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken)
    {
        var files = LibraryArchiveTreeExtensions.GetTree(libraryArchive).GetFiles().ToArray();

        // A mod's files lie in a subfolder of natives or reframework; a wrapper folder named after the loader holds only its files.
        var holdsMod = files.Any(file =>
        {
            var parts = file.Item.Path.Parts.Select(part => part.ToString()).ToArray();
            for (var i = 0; i + 2 < parts.Length; i++)
            {
                if (parts[i].Equals(ReFrameworkFiles.Natives, StringComparison.OrdinalIgnoreCase)
                    || parts[i].Equals(ReFrameworkFiles.Root, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        });
        var loader = files.FirstOrDefault(file =>
            file.Item.Path.FileName.ToString().Equals(ReFrameworkFiles.Loader, StringComparison.OrdinalIgnoreCase)
            && file.Item.Path.Parts.Count() <= 2);

        if (holdsMod || loader is null)
            return ValueTask.FromResult<InstallerResult>(new NotSupported(Reason: $"The archive is not the REFramework loader: it has no {ReFrameworkFiles.Loader} at its root, or it holds a mod's files as well."));

        loader.ToLoadoutFile(loadout, loadoutGroup, transaction, new GamePath(LocationId.Game, ReFrameworkFiles.Loader));
        return ValueTask.FromResult<InstallerResult>(new Success());
    }
}
