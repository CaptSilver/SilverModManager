using NexusMods.Paths;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NexusMods.Wabbajack.Acquire;
using NexusMods.Wabbajack.Install;

namespace NexusMods.App.UI.Pages.Wabbajack;

/// <summary>A line of text for the page and whether it should be shown as a warning.</summary>
public readonly record struct WabbajackNote(string Text, bool IsWarning);

/// <summary>The decisions and wording of the modlist page that don't need a view model around them.</summary>
public static class WabbajackModlistText
{
    /// <summary>The line for the current phase of an install run.</summary>
    public static string Progress(ModlistInstallProgress.Snapshot snapshot) => snapshot.Phase switch
    {
        InstallPhase.Check => "Checking the list",
        InstallPhase.Resolve => $"Locating files in archives: {snapshot.DoneItems} of {snapshot.TotalItems}",
        InstallPhase.Write => $"Writing files: {snapshot.DoneItems} of {snapshot.TotalItems}",
        InstallPhase.Order => "Ordering mods and plugins",
        _ => throw new ArgumentOutOfRangeException(nameof(snapshot), snapshot.Phase, "Unknown install phase"),
    };

    /// <summary>How far through its own phase the run is, 0 to 1. Phases with no items count as not started.</summary>
    public static double Fraction(ModlistInstallProgress.Snapshot snapshot)
        => snapshot.TotalItems <= 0 ? 0 : Math.Clamp((double)snapshot.DoneItems / snapshot.TotalItems, 0, 1);

    /// <summary>The line for the archive downloads the acquirer is running.</summary>
    public static string Downloads(AcquireProgress progress)
        => progress.Total == 0 ? "" : $"Downloading archives: {progress.Finished} of {progress.Total}";

    /// <summary>
    /// What the extracted files will take, next to what the archive location has left. Running short is a warning,
    /// never a refusal: the estimate is a guess, and the user may be about to free the room.
    /// </summary>
    /// <param name="isLowerBound">An archive is not in the library yet, so the estimate counts its download size, which is the least it can come to.</param>
    /// <param name="free">Null when the free space could not be read.</param>
    public static WabbajackNote FreeSpace(Size estimate, bool isLowerBound, Size? free)
    {
        var needed = $"{(isLowerBound ? "Extracted size: at least " : "Extracted size: ")}{estimate}";
        if (free is not { } available) return new WabbajackNote(needed, false);

        return available < estimate
            ? new WabbajackNote($"{needed}. Not enough free space: {available} free", true)
            : new WabbajackNote($"{needed}, {available} free", false);
    }

    /// <summary>
    /// The extracted size of the archives of a plan: what the library holds for an archive it has, the download size
    /// for one it does not (which makes the estimate a lower bound). A game's own files take nothing.
    /// </summary>
    public static (Size Estimate, bool IsLowerBound) ExtractedSize(ArchivePlan plan)
    {
        ulong total = 0;
        var isLowerBound = false;
        foreach (var row in plan.Rows)
        {
            switch (row.State)
            {
                case ArchiveState.Have:
                    // A row without its library file is a held archive we cannot measure; its download size is the best figure left.
                    total += row.Library is { } library && library.TryGetAsLibraryArchive(out var archive)
                        ? archive.Children.Aggregate(0UL, (sum, entry) => sum + entry.AsLibraryFile().Size.Value)
                        : row.Library?.Size.Value ?? (ulong)row.Archive.Size;
                    break;
                case ArchiveState.GameFile or ArchiveState.GameFileVersion:
                    break;
                default:
                    total += (ulong)row.Archive.Size;
                    isLowerBound = true;
                    break;
            }
        }
        return (Size.From(total), isLowerBound);
    }

    /// <summary>The same test the install job applies: only archives in the library and the game's own files count as available.</summary>
    public static bool IsAvailable(ArchivePlanRow row) => row.State is ArchiveState.Have or ArchiveState.GameFile;

    /// <summary>Whether Install may start: every archive is available, or the user chose to go without the missing ones.</summary>
    public static bool CanInstall(ArchivePlan plan, bool skipMissingArchives)
        => skipMissingArchives || plan.Rows.All(IsAvailable);

    /// <summary>The loadouts among <paramref name="loadouts"/> whose game is <paramref name="game"/>, in the order given.</summary>
    public static IReadOnlyList<LoadoutId> LoadoutsFor(IEnumerable<(LoadoutId Id, GameId Game)> loadouts, GameId game)
        => loadouts.Where(loadout => loadout.Game == game).Select(loadout => loadout.Id).ToArray();

    /// <summary>What the state column says for a row.</summary>
    public static string State(ArchivePlanRow row) => row.State switch
    {
        ArchiveState.Have => "In library",
        ArchiveState.Automatic => "Downloads automatically",
        ArchiveState.Browser => row.Opened ? "Waiting for your download" : "Needs a download in your browser",
        ArchiveState.Manual => "Needs a manual download",
        ArchiveState.GameFile => "Game file",
        ArchiveState.GameFileVersion => "Game file differs",
        ArchiveState.Unsupported => "Not available",
        _ => throw new ArgumentOutOfRangeException(nameof(row), row.State, "Unknown archive state"),
    };
}
