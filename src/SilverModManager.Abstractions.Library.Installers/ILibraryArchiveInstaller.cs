using JetBrains.Annotations;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Library.Installers;

/// <summary>
/// Variant of <see cref="ILibraryFileInstaller"/> for <see cref="LibraryArchive"/>
/// </summary>
[PublicAPI]
public interface ILibraryArchiveInstaller : ILibraryFileInstaller
{
    /// <summary>
    /// Checks whether the provided library archive is supported by this installer.
    /// </summary>
    /// <remarks>
    /// This method should only do surface checks on the entity itself.
    /// </remarks>
    bool IsSupportedLibraryArchive(LibraryArchive.ReadOnly libraryArchive);

    /// <summary>
    /// Executes the installer.
    /// </summary>
    ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken);
}
