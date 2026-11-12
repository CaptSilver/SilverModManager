using System.Collections.Immutable;
using System.Reactive.Linq;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Diagnostics;

/// <summary>
/// Runs the <see cref="DiagnosticAction"/> with a given id. Emitters only name an action; the handler is what makes
/// it a button (or a command line fix), and an action without a handler shows none.
/// </summary>
public interface IDiagnosticActionHandler
{
    /// <summary>
    /// The <see cref="DiagnosticAction.Id"/> this handler runs.
    /// </summary>
    string ActionId { get; }

    /// <summary>
    /// True when <see cref="ExecuteAsync"/> never asks the user anything or opens a window, so a command line run can
    /// start it and wait for it. A handler that shows a confirmation or a result dialog keeps the default.
    /// </summary>
    bool RunsUnattended => false;

    /// <summary>
    /// The button text for <paramref name="action"/> when the handler has a localized one; null to show the action's own label.
    /// </summary>
    string? LabelFor(DiagnosticAction action) => null;

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
