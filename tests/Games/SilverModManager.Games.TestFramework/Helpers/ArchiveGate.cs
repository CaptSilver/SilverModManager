using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.TestFramework.Helpers;

/// <summary>
/// Stands in for one of a game's installers and holds, releases or fails the install of one named archive, so a
/// test can stop a collection install in the middle without a guided installer window. Archives with other names go
/// straight to the installer it replaced.
/// </summary>
public sealed class ArchiveGate : ILibraryItemInstaller, IDisposable
{
    private readonly ILibraryItemInstaller[] _installers;
    private readonly int _slot;
    private readonly string _archiveName;
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ArchiveGate(ILibraryItemInstaller[] installers, int slot, string archiveName)
    {
        _installers = installers;
        _slot = slot;
        _archiveName = archiveName;
        Inner = installers[slot];
    }

    /// <summary>
    /// Puts a gate for <paramref name="archiveName"/> in <paramref name="installers"/> at <paramref name="slot"/>; disposing it puts the
    /// original installer back. Gate slot 1, not 0: the collection install finds the FOMOD installer in slot 0 by type, and a gate there would hide it.
    /// </summary>
    public static ArchiveGate Install(ILibraryItemInstaller[] installers, int slot, string archiveName)
    {
        var gate = new ArchiveGate(installers, slot, archiveName);
        installers[slot] = gate;
        return gate;
    }

    /// <summary>The installer this gate replaced.</summary>
    public ILibraryItemInstaller Inner { get; }

    /// <summary>Completes when the gated archive reaches <see cref="ExecuteAsync"/>.</summary>
    public Task Entered => _entered.Task;

    /// <summary>When set before the run, the gated archive throws this at once instead of waiting.</summary>
    public Exception? ThrowsOnEntry { get; set; }

    /// <summary>Lets the held install go on to the installer this gate replaced.</summary>
    public void Release() => _outcome.TrySetResult();

    /// <summary>Makes the held (or the next) install of the gated archive throw <paramref name="exception"/>.</summary>
    public void Fail(Exception exception) => _outcome.TrySetException(exception);

    /// <inheritdoc/>
    public bool IsSupportedLibraryItem(LibraryItem.ReadOnly libraryItem) =>
        IsGated(libraryItem) || Inner.IsSupportedLibraryItem(libraryItem);

    /// <inheritdoc/>
    public async ValueTask<InstallerResult> ExecuteAsync(
        LibraryItem.ReadOnly libraryItem, LoadoutItemGroup.New loadoutGroup, ITransaction transaction,
        Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        if (!IsGated(libraryItem))
            return await Inner.ExecuteAsync(libraryItem, loadoutGroup, transaction, loadout, cancellationToken);

        _entered.TrySetResult();
        if (ThrowsOnEntry is { } exception) throw exception;

        await _outcome.Task.WaitAsync(cancellationToken);

        // The inner installer may not take the archive; asking it to run anyway would throw instead of declining.
        if (!Inner.IsSupportedLibraryItem(libraryItem)) return new NotSupported($"{Inner.GetType().Name} does not take {_archiveName}");
        return await Inner.ExecuteAsync(libraryItem, loadoutGroup, transaction, loadout, cancellationToken);
    }

    /// <summary>Puts the replaced installer back in its slot.</summary>
    public void Dispose() => _installers[_slot] = Inner;

    private bool IsGated(LibraryItem.ReadOnly libraryItem) => libraryItem.Name == _archiveName;
}
