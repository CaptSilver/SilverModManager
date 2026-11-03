using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Library.Installers;

/// <summary>
/// An installer that can run without anyone to ask. An unattended install calls
/// <see cref="ExecuteUnattendedAsync"/> instead of <see cref="ILibraryItemInstaller.ExecuteAsync"/>.
/// </summary>
public interface IUnattendedInstaller : ILibraryItemInstaller
{
    /// <summary>
    /// Same as <see cref="ILibraryItemInstaller.ExecuteAsync"/>, but throws <see cref="ReviewRequiredException"/>
    /// with <see cref="ReviewKind.NeedsChoices"/> where the installer would have asked a person.
    /// </summary>
    ValueTask<InstallerResult> ExecuteUnattendedAsync(
        LibraryItem.ReadOnly libraryItem,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken);
}
