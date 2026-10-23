using JetBrains.Annotations;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Library.Installers;

/// <summary>
/// Variant of <see cref="ILibraryItemInstaller"/> for <see cref="LibraryFile"/>.
/// </summary>
[PublicAPI]
public interface ILibraryFileInstaller : ILibraryItemInstaller
{
    /// <summary>
    /// Checks whether the provided library file is supported by this installer.
    /// </summary>
    /// <remarks>
    /// This method should only do surface checks on the entity itself.
    /// </remarks>
    bool IsSupportedLibraryFile(LibraryFile.ReadOnly libraryFile);

    /// <summary>
    /// Executes the installer.
    /// </summary>
    ValueTask<InstallerResult> ExecuteAsync(
        LibraryFile.ReadOnly libraryFile,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken);
}
