using JetBrains.Annotations;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Diagnostics.Emitters;

/// <summary>
/// An emitter whose diagnostics come from files the user can put away, such as old crash logs. Clearing puts away
/// all of them, including ones too old to show, so their diagnostics are gone at the next refresh.
/// </summary>
[PublicAPI]
public interface IClearableDiagnosticSource : IDiagnosticEmitter
{
    /// <summary>The button text, such as "Clear crash reports".</summary>
    string ClearLabel { get; }

    /// <summary>Whether there is anything to clear for the loadout. Reads the disk, so call it off the UI thread.</summary>
    bool HasAnythingToClear(Loadout.ReadOnly loadout);

    /// <summary>
    /// Clears what there is and returns one sentence saying what was done, for the page to show. An
    /// <see cref="IOException"/>, <see cref="UnauthorizedAccessException"/> or <see cref="InvalidOperationException"/>
    /// is shown on the page as a failed clear; anything else is a bug.
    /// </summary>
    Task<string> ClearAsync(Loadout.ReadOnly loadout, CancellationToken cancellationToken);
}
