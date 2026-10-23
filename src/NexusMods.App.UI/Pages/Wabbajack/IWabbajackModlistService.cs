using Avalonia.Media.Imaging;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Install;

namespace NexusMods.App.UI.Pages.Wabbajack;

/// <summary>What the page shows about a list before any loadout is involved.</summary>
/// <param name="GameType">The list's game as Wabbajack names it.</param>
/// <param name="Game">The app's title for it, or null when the app has none.</param>
/// <param name="Banner">The list's <c>modlist-image.png</c>, if it has one.</param>
public sealed record WabbajackModlistDetails(
    string Name,
    string Author,
    string Version,
    string GameType,
    GameId? Game,
    int ArchiveCount,
    Size ArchiveSize,
    Size InstallSize,
    Bitmap? Banner);

/// <summary>An install in flight.</summary>
public interface IWabbajackInstallRun
{
    /// <summary>Every snapshot of the job's progress, starting with the current one.</summary>
    IObservable<ModlistInstallProgress.Snapshot> Progress { get; }

    /// <summary>Ends when the job does; faults with the job's exception.</summary>
    Task Completion { get; }
}

/// <summary>
/// Everything the modlist page needs from the library, the games and the install job, behind one seam so the view
/// model can be driven without any of them.
/// </summary>
public interface IWabbajackModlistService
{
    Task<WabbajackModlistDetails> Open(LibraryFileId file, CancellationToken cancellationToken);

    /// <summary>
    /// Matches the list's archives to the library and the loadout's game files. A null <paramref name="target"/> is a
    /// loadout that does not exist yet: the game files come from the game's installation.
    /// </summary>
    Task<ArchivePlan> BuildPlan(LibraryFileId file, LoadoutId? target, CancellationToken cancellationToken);

    /// <summary>The files of the game folder the list would overwrite, which exist in the loadout's game folder now.</summary>
    Task<IReadOnlyList<RelativePath>> GameFilesReplaced(LibraryFileId file, LoadoutId? target, CancellationToken cancellationToken);

    /// <summary>Adds a loadout for <paramref name="game"/> and returns its id. Nothing is created before this is called.</summary>
    Task<LoadoutId> CreateLoadout(GameId game, CancellationToken cancellationToken);

    IWabbajackInstallRun StartInstall(LibraryFileId file, LoadoutId target, InstallOptions options);

    /// <summary>What is left on the filesystem the library keeps its files on, or null if it cannot be read.</summary>
    Size? FreeSpace();

    /// <summary>Adds what is in the inbox folder to the library and lets the current plan notice it.</summary>
    /// <returns>The files left in the inbox because no archive of the plan matched them.</returns>
    Task<IReadOnlyList<UnmatchedInbox>> CheckInbox(CancellationToken cancellationToken);

    void OpenInboxFolder();

    /// <summary>Copies files into the inbox folder, then checks it.</summary>
    Task<IReadOnlyList<UnmatchedInbox>> AddToInbox(IReadOnlyList<AbsolutePath> files, CancellationToken cancellationToken);
}
