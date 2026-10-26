using System.Collections.Immutable;
using System.Reactive.Linq;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Runs the <see cref="SilverModManager.Abstractions.Diagnostics.DiagnosticAction"/> with a given id. Emitters only
/// name an action; the handler is what makes it a button, and an action without a handler shows none.
/// </summary>
public interface IDiagnosticActionHandler
{
    /// <summary>
    /// The <see cref="SilverModManager.Abstractions.Diagnostics.DiagnosticAction.Id"/> this handler runs.
    /// </summary>
    string ActionId { get; }

    /// <summary>
    /// Runs the action for the loadout whose diagnostic offered it. Completes once the fix is done and the
    /// diagnostics have been refreshed.
    /// </summary>
    Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments);

    /// <summary>
    /// What the action is doing for the loadout right now, shown next to its button while it runs; null when it is
    /// idle. Handlers that finish at once have nothing to say.
    /// </summary>
    IObservable<string?> StatusOf(LoadoutId loadoutId) => Observable.Return<string?>(null);
}
