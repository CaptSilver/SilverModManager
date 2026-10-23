using JetBrains.Annotations;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Diagnostics;

/// <summary>
/// A diagnostic manager, which keeps track of all current diagnostics and refreshes them if necessary.
/// </summary>
[PublicAPI]
public interface IDiagnosticManager : IDisposable
{
    /// <summary>
    /// Returns an observable stream of all diagnostics for a loadout.
    /// </summary>
    IObservable<Diagnostic[]> GetLoadoutDiagnostics(LoadoutId loadoutId);

    /// <summary>
    /// Runs the emitters for a loadout again even though nothing in the database changed, and
    /// completes once the new result has been published to <see cref="GetLoadoutDiagnostics"/>.
    /// </summary>
    /// <remarks>
    /// Diagnostics read inputs the database does not track (launch options, files on disk), so a fix
    /// made outside the app only shows up when something asks for a rerun. A call made while a refresh
    /// is still running joins that refresh instead of starting another.
    /// </remarks>
    Task RefreshAsync(LoadoutId loadoutId);

    /// <summary>
    /// Returns the counts of the diagnostics per severity level.
    /// </summary>
    IObservable<(int NumSuggestions, int NumWarnings, int NumCritical)> CountDiagnostics(LoadoutId loadoutId);
}
