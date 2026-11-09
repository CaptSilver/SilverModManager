using System.Globalization;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Install;
using SilverModManager.App.UI.Resources;

namespace SilverModManager.App.UI.Pages.Wabbajack;

/// <summary>A line of text for the page and whether it should be shown as a warning.</summary>
public readonly record struct WabbajackNote(string Text, bool IsWarning);

/// <summary>What the line under the toolbar says. <paramref name="Fraction"/> is null when there is nothing to draw a bar from.</summary>
public readonly record struct WabbajackStatus(string Text, double? Fraction, bool IsIndeterminate, bool IsWarning);

/// <summary>What the list itself says about itself: its description and, when it gives a link, its readme.</summary>
public sealed record WabbajackModlistBlurb(string Description, Uri? Readme);

/// <summary>What a finished check found, for the log: archives by what is left to do about them.</summary>
public readonly record struct WabbajackPlanCounts(int Ready, int ToGet, int ByHand, int GameFilesDiffer, int NotNeeded = 0);

/// <summary>The decisions and wording of the modlist page that don't need a view model around them.</summary>
public static class WabbajackModlistText
{
    /// <summary>The line for the current phase of an install run.</summary>
    public static string Progress(ModlistInstallProgress.Snapshot snapshot)
    {
        // Archives build side by side and each is one Write item that can run for minutes. Naming whichever reported
        // last made the line jump between numbers, so it names the oldest running one and counts the rest.
        if (snapshot.Builds is { } builds)
        {
            var running = builds.Running == 1 ? "1 in progress" : $"{builds.Running:N0} in progress";
            var shown = builds.Shown;
            return string.Create(CultureInfo.CurrentCulture, $"Building archives: {builds.Done:N0} of {builds.Count:N0} done, {running} \u00b7 {shown.Name} ({shown.DoneFiles:N0} of {shown.TotalFiles:N0} files)");
        }

        return snapshot.Phase switch
        {
            InstallPhase.Check => "Checking the list",
            InstallPhase.Resolve => $"Locating files in archives: {snapshot.DoneItems} of {snapshot.TotalItems}",
            InstallPhase.Write => $"Writing files: {snapshot.DoneItems} of {snapshot.TotalItems}",
            InstallPhase.Order => "Ordering mods and plugins",
            _ => throw new ArgumentOutOfRangeException(nameof(snapshot), snapshot.Phase, "Unknown install phase"),
        };
    }

    /// <summary>How far through its own phase the run is, 0 to 1. Phases with no items count as not started. The files packed into archives still being built count as part of those items, so the bar keeps moving through a long build.</summary>
    public static double Fraction(ModlistInstallProgress.Snapshot snapshot)
        => snapshot.TotalItems <= 0 ? 0 : Math.Clamp((snapshot.DoneItems + snapshot.ArchiveItems) / snapshot.TotalItems, 0, 1);

    /// <summary>The line for the phase of the check the page is waiting on.</summary>
    public static string Checking(PlanProgress progress) => progress.Phase switch
    {
        PlanPhase.ReadingList => "Reading the list",
        PlanPhase.CheckingLibrary => progress.Total == 0 ? "Checking archives against your library" : $"Checking archives against your library: {progress.Done} of {progress.Total}",
        PlanPhase.HashingGameFiles => $"Hashing game files: {Bytes(progress.Done, progress.Total)}{(progress.Item is { Length: > 0 } item ? $" ({item})" : "")}",
        PlanPhase.CheckingGameFolder => progress.Total == 0 ? "Looking for the list's files in your game folder" : $"Looking for the list's files in your game folder: {progress.Done} of {progress.Total}",
        _ => throw new ArgumentOutOfRangeException(nameof(progress), progress.Phase, "Unknown check phase"),
    };

    // Both numbers in the unit of the total, so "2.1 of 8.4 GB" reads as one measure.
    private static string Bytes(long done, long total)
    {
        var (divisor, unit) = total >= 1_000_000_000 ? (1e9, "GB") : (1e6, "MB");
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{done / divisor:0.0} of {total / divisor:0.0} {unit}");
    }

    /// <summary>How far through its own phase the check is, 0 to 1. A phase with no total reads 0.</summary>
    public static double CheckFraction(PlanProgress progress)
        => progress.Total <= 0 ? 0 : Math.Clamp((double)progress.Done / progress.Total, 0, 1);

    /// <summary>The phase has no count to draw a bar from, so the bar should only show that work is going on.</summary>
    public static bool IsIndeterminate(PlanProgress progress) => progress.Total <= 0;

    /// <summary>Sorts the rows of a plan into the groups the log reports.</summary>
    public static WabbajackPlanCounts Counts(ArchivePlan plan) => new(
        plan.Rows.Count(row => row.State is ArchiveState.Have or ArchiveState.GameFile),
        plan.Rows.Count(row => row.State is ArchiveState.Automatic or ArchiveState.Browser or ArchiveState.SignIn),
        plan.Rows.Count(row => row.State is ArchiveState.Manual or ArchiveState.Unsupported),
        plan.Rows.Count(row => row.State == ArchiveState.GameFileVersion),
        plan.Rows.Count(row => row.State == ArchiveState.NotNeeded));

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
                case ArchiveState.GameFile or ArchiveState.GameFileVersion or ArchiveState.NotNeeded:
                    break;
                default:
                    total += (ulong)row.Archive.Size;
                    isLowerBound = true;
                    break;
            }
        }
        return (Size.From(total), isLowerBound);
    }

    /// <summary>
    /// The same test the install job applies (<see cref="ArchiveCheck.Of"/>): archives in the library and the game's own
    /// files count as available. A game file that differs is available too: the job skips its directives instead of
    /// needing a download, and putting the right version back is not something the page can ask for.
    /// </summary>
    public static bool IsAvailable(ArchivePlanRow row) => row.State is ArchiveState.Have or ArchiveState.GameFile or ArchiveState.GameFileVersion or ArchiveState.NotNeeded;

    /// <summary>Whether Install may start: every archive is available, or the user chose to go without the missing ones.</summary>
    public static bool CanInstall(ArchivePlan plan, bool skipMissingArchives)
        => skipMissingArchives || plan.Rows.All(IsAvailable);

    /// <summary>
    /// What the page header says while a step runs: the install, else the check, else the archive downloads. The bar
    /// and the line come from the same step, so they never describe two. Empty text when nothing runs.
    /// </summary>
    public static WabbajackStatus Running(
        WabbajackInstallStatus install,
        string checkText, double checkFraction, bool isCheckIndeterminate,
        bool isDownloading, AcquireProgress? downloads)
    {
        if (!string.IsNullOrEmpty(install.Text)) return new WabbajackStatus(install.Text, install.Fraction, false, false);
        if (checkText != "") return new WabbajackStatus(checkText, checkFraction, isCheckIndeterminate, false);
        // A batch that ended early leaves its count part done; only a running batch is worth a header.
        if (isDownloading && downloads is { Total: > 0 } running && running.Finished < running.Total)
            return new WabbajackStatus(Downloads(running), (double)running.Finished / running.Total, false, false);
        return new WabbajackStatus("", null, false, false);
    }

    /// <summary>
    /// The one line under the toolbar when no step runs: an error, or the one thing that blocks the buttons. While a
    /// step runs (see <see cref="Running"/>) the page header shows it with a bar and this line stays empty, except for
    /// an error. A button's own reason stays in its tooltip.
    /// </summary>
    /// <param name="installBlockedReason">Wins over Download's because it names the next step towards the goal.</param>
    /// <param name="gameFilesNote">Comes after the install reason and before Download's, since a plan of only the game's own files would otherwise show Download's "nothing to fetch" line and never this one.</param>
    /// <param name="planSummary">What is left to get; shown when both Install and Download are allowed.</param>
    public static WabbajackStatus Status(
        string errorText,
        string installProgressText,
        string checkText, double checkFraction, bool isCheckIndeterminate,
        bool isDownloading, AcquireProgress? downloads,
        string installBlockedReason, WabbajackNote gameFilesNote, string downloadBlockedReason,
        string planSummary)
    {
        if (errorText != "") return new WabbajackStatus(errorText, null, false, true);
        var running = Running(new WabbajackInstallStatus(installProgressText, 0), checkText, checkFraction, isCheckIndeterminate, isDownloading, downloads);
        if (running.Text != "") return new WabbajackStatus("", null, false, false);
        if (installBlockedReason != "") return new WabbajackStatus(installBlockedReason, null, false, false);
        if (!string.IsNullOrEmpty(gameFilesNote.Text)) return new WabbajackStatus(gameFilesNote.Text, null, false, gameFilesNote.IsWarning);
        if (downloadBlockedReason != "") return new WabbajackStatus(downloadBlockedReason, null, false, false);
        return new WabbajackStatus(planSummary, null, false, false);
    }

    /// <summary>
    /// Tells the user their game differs from the one the list was made for, and what the install does about it. Empty
    /// unless every archive is available: the status line holds one thing, and "N of M still to get" with its "Show
    /// them" button matters more while something is still missing.
    /// </summary>
    public static WabbajackNote GameFilesNote(ArchivePlan plan)
    {
        // A file the game lacks has no copy of its own to use instead, so the sentences below would not be true of it.
        var present = plan.Rows.Where(row => row.State == ArchiveState.GameFileVersion && !row.GameFileAbsent).ToArray();
        var replaced = present.Where(row => row.GameFileReplaced).ToArray();
        var differing = present.Length - replaced.Length;
        if (present.Length == 0 || !plan.Rows.All(IsAvailable)) return new WabbajackNote("", false);

        var sentences = new List<string>(2);
        if (differing == 1)
            sentences.Add("Your game is a different version from the one the list was made for, so 1 of its files is left out and your game's own copy is used instead. Anything the list builds from it is listed under \"Files that were skipped\" after the install.");
        else if (differing > 1)
            sentences.Add($"Your game is a different version from the one the list was made for, so {differing} of its files are left out and your game's own copies are used instead. Anything the list builds from them is listed under \"Files that were skipped\" after the install.");

        if (replaced.Length > 0)
        {
            var verifyWhere = GameFileReplacement.VerifyWhere(replaced[0].InstalledFrom);
            sentences.Add(replaced.Length == 1
                ? $"1 of your game's files is not the original the list needs: something replaced it, {GameFileReplacement.Cause}, and the app has no copy to go back to, so it is left out. Verify the game's files {verifyWhere} to put it back before you install; until then anything the list builds from it is listed under \"Files that were skipped\" after the install."
                : $"{replaced.Length} of your game's files are not the originals the list needs: something replaced them, {GameFileReplacement.Cause}, and the app has no copies to go back to, so they are left out. Verify the game's files {verifyWhere} to put them back before you install; until then anything the list builds from them is listed under \"Files that were skipped\" after the install.");
        }

        var text = string.Join(" ", sentences);
        return new WabbajackNote(text, true);
    }

    /// <summary>The "what is still to get" line while nothing is running.</summary>
    public static string PlanSummary(ArchivePlan plan)
    {
        var missing = plan.Rows.Count(row => !IsAvailable(row));
        return missing == 0 ? "Every archive is available" : $"{missing} of {Wanted(plan)} archives still to get";
    }

    /// <summary>The description trimmed, and the readme only if it is an https link (Wabbajack lists put a URL there).</summary>
    public static WabbajackModlistBlurb Blurb(string description, string readme)
        => new(description.Trim(), Uri.TryCreate(readme, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null);

    private const string CheckingTheList = "Checking the list against your library and game files. This can take a while for a big list.";
    private const string CheckFailed = "The list could not be checked.";

    /// <summary>Why Install is off, as a sentence for the page, or "" while it is allowed.</summary>
    public static string InstallBlockedReason(bool hasLoadout, bool isPlanning, bool planReady, bool isInstalling, ArchivePlan plan, bool skipMissingArchives, string? supersededBy = null, string? otherVersion = null)
    {
        if (isInstalling) return "An install is running.";
        if (supersededBy is not null) return string.Format(Language.CollectionUpdate_SupersededBlocked, supersededBy);
        if (otherVersion is not null) return string.Format(Language.CollectionUpdate_OtherVersionBlocked, otherVersion);
        if (isPlanning) return CheckingTheList;
        if (!planReady) return CheckFailed;
        if (!hasLoadout) return "Pick a loadout to install the collection into.";
        if (CanInstall(plan, skipMissingArchives)) return "";

        var incomplete = plan.Rows.Count(row => !IsAvailable(row) && row.HoldsIncompleteCopy);
        var missing = plan.Rows.Count(row => !IsAvailable(row)) - incomplete;
        var parts = new List<string>();
        if (missing > 0) parts.Add($"{missing} of {Wanted(plan)} {(missing == 1 ? "archives is" : "archives are")} not in your library yet.");
        if (incomplete > 0) parts.Add(incomplete == 1
            ? "1 archive needs downloading again: the library copy is missing archives packed inside it."
            : $"{incomplete} archives need downloading again: the library copies are missing archives packed inside them.");
        parts.Add("Get what is missing, or tick \"Skip files from missing archives\".");
        return string.Join(" ", parts);
    }

    /// <summary>The archives the install reads: all but Mod Organizer 2, which is never installed.</summary>
    private static int Wanted(ArchivePlan plan) => plan.Rows.Count(row => row.State != ArchiveState.NotNeeded);

    /// <summary>Why Download is off, as a sentence for the page, or "" while it is allowed.</summary>
    public static string DownloadBlockedReason(bool isPlanning, bool isPreparing, bool planReady, bool isDownloading, ArchivePlan plan, bool isInstalling = false)
    {
        if (isInstalling) return "An install is running.";
        if (isDownloading) return "Downloads are running.";
        // Download plans the list afresh before it starts, which takes as long as the first check.
        if (isPlanning || isPreparing) return CheckingTheList;
        if (!planReady) return CheckFailed;
        if (plan.Rows.Any(row => row.State is ArchiveState.Automatic or ArchiveState.Browser)) return "";
        if (plan.Rows.Count == 0) return "The list names no archives.";

        var byHand = plan.Rows.Count(row => row.State is ArchiveState.Manual or ArchiveState.Unsupported);
        var signIn = plan.Rows.Count(row => row.State == ArchiveState.SignIn);
        if (byHand == 0 && signIn == 0) return "Every archive is already in your library or is a file of your game.";

        var parts = new List<string>();
        if (signIn > 0) parts.Add(signIn == 1 ? "The missing archive is on a forum you need to sign in to first." : $"{signIn} missing archives are on forums you need to sign in to first.");
        if (byHand > 0)
        {
            parts.Add(byHand == 1 ? "The missing archive cannot be fetched by the app." : $"None of the {byHand} missing archives can be fetched by the app.");
            parts.Add("Each needs a manual download: use Open page on its row, or drop the file on this page.");
        }
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Why "Open next five" is off, as a sentence for the page, or "" while it is allowed. When Download cannot be
    /// pressed either, pointing at it would send the user nowhere, so its own reason is repeated.
    /// </summary>
    public static string OpenNextFiveBlockedReason(bool hasStartedDownloads, string downloadBlockedReason, ArchivePlan plan)
    {
        if (hasStartedDownloads) return "";
        if (downloadBlockedReason != "") return downloadBlockedReason;
        if (!plan.Rows.Any(row => row.State == ArchiveState.Browser)) return "No archive needs your browser.";
        return "Press Download first. The app only expects your browser downloads once it has started them.";
    }

    /// <summary>The loadouts among <paramref name="loadouts"/> whose game is <paramref name="game"/>, in the order given.</summary>
    public static IReadOnlyList<LoadoutId> LoadoutsFor(IEnumerable<(LoadoutId Id, GameId Game)> loadouts, GameId game)
        => loadouts.Where(loadout => loadout.Game == game).Select(loadout => loadout.Id).ToArray();

    /// <summary>What the state column says for a row.</summary>
    public static string State(ArchivePlanRow row) => row.State switch
    {
        ArchiveState.Have => "In library",
        ArchiveState.Automatic => "Downloads automatically",
        ArchiveState.Browser => row.Opened ? "Waiting for your download" : "Needs a download in your browser",
        ArchiveState.SignIn => "Needs you to sign in",
        ArchiveState.Manual => "Needs a manual download",
        ArchiveState.GameFile => "Game file",
        ArchiveState.GameFileVersion => row.GameFileReplaced ? "Game file replaced" : "Game file differs",
        ArchiveState.Unsupported => "Not available",
        ArchiveState.NotNeeded => "Not needed: Mod Organizer 2 itself",
        _ => throw new ArgumentOutOfRangeException(nameof(row), row.State, "Unknown archive state"),
    };
}
