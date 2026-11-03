using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.AdvancedInstaller;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.TestFramework;

/// <summary>
/// Stands in for the Advanced Manual Installer window: counts how often an install reached it and declines
/// every archive, so a test can tell "opened the manual installer" from "never asked".
/// </summary>
public sealed class CountingAdvancedInstallerHandler : IAdvancedInstallerHandler
{
    private int _calls;

    /// <summary>How many times an install asked this handler to install something.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <inheritdoc />
    public bool WasOpenedDirectly { get; set; }

    /// <inheritdoc />
    public bool IsSupportedLibraryItem(LibraryItem.ReadOnly libraryItem) => true;

    /// <inheritdoc />
    public bool IsSupportedLibraryFile(LibraryFile.ReadOnly libraryFile) => true;

    /// <inheritdoc />
    public bool IsSupportedLibraryArchive(LibraryArchive.ReadOnly libraryArchive) => true;

    /// <inheritdoc />
    public ValueTask<InstallerResult> ExecuteAsync(
        LibraryItem.ReadOnly libraryItem,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken) => Decline();

    /// <inheritdoc />
    public ValueTask<InstallerResult> ExecuteAsync(
        LibraryFile.ReadOnly libraryFile,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken) => Decline();

    /// <inheritdoc />
    public ValueTask<InstallerResult> ExecuteAsync(
        LibraryArchive.ReadOnly libraryArchive,
        LoadoutItemGroup.New loadoutGroup,
        ITransaction transaction,
        Loadout.ReadOnly loadout,
        CancellationToken cancellationToken) => Decline();

    private ValueTask<InstallerResult> Decline()
    {
        Interlocked.Increment(ref _calls);
        return ValueTask.FromResult<InstallerResult>(new NotSupported("counting handler"));
    }
}
