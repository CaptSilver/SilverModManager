using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Library;

/// <summary>
/// What an install did with one library item: whether this call downloaded it and what happened to it in the loadout.
/// <see cref="Name"/> is how the source names the item (a Thunderstore version, a Nexus file name).
/// </summary>
public readonly record struct InstallOutcome(string Name, LibraryItem.ReadOnly Item, bool Downloaded, LoadoutPackageOutcome Outcome, bool Dependency);

/// <summary>
/// One item of an install: the library copy when there is one, otherwise how to fetch it.
/// </summary>
/// <param name="InLibrary">The copy already in the library, or null when <paramref name="Download"/> has to fetch it.</param>
/// <param name="Dependency">Whether the item is there because something else needs it, not because it was asked for.</param>
public sealed record ResolvedInstall(string Name, bool Dependency, LibraryItem.ReadOnly? InLibrary, Func<CancellationToken, Task<LibraryItem.ReadOnly>> Download);

/// <summary>
/// The install step every mod source shares, so a restore point and a repeated request mean the same everywhere.
/// </summary>
public interface ILoadoutInstallCore
{
    /// <summary>
    /// Downloads every item the library lacks, then writes one restore point, then installs or enables each item in
    /// list order. A download that fails leaves the loadout and its restore points untouched. A second call with the
    /// same <paramref name="inFlightKey"/> for the same loadout while a run is in progress joins that run and gets its
    /// outcomes, so a page click and a retried request do not download twice.
    /// </summary>
    /// <exception cref="LibraryItemInDisabledGroupException">An item is only linked inside a disabled group.</exception>
    Task<IReadOnlyList<InstallOutcome>> Run(LoadoutId loadout, string inFlightKey, IReadOnlyList<ResolvedInstall> items, CancellationToken token);
}
