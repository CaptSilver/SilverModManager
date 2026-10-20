using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Loadouts;
using NexusMods.App.UI.WorkspaceSystem;
using R3;

namespace NexusMods.App.UI.Pages.Tools;

public interface IToolsPageViewModel : IPageViewModelInterface
{
    LoadoutId LoadoutId { get; set; }

    /// <summary>
    /// The tools registered for the loadout's game.
    /// </summary>
    IReadOnlyList<ITool> Tools { get; }

    /// <summary>
    /// Runs one of <see cref="Tools"/> the way the tool manager runs any tool.
    /// </summary>
    ReactiveCommand<ITool> RunToolCommand { get; }

    /// <summary>
    /// Asks for a Windows program and runs it in the game's prefix.
    /// </summary>
    ReactiveCommand<Unit> RunWindowsProgramCommand { get; }

    /// <summary>
    /// Whether <see cref="RunWindowsProgramCommand"/> can run for this loadout's game.
    /// </summary>
    bool CanRunWindowsProgram { get; }

    /// <summary>
    /// Why no Windows program can be run, set when <see cref="CanRunWindowsProgram"/> is false.
    /// </summary>
    string? Diagnostic { get; }

    /// <summary>
    /// The Wine prefix programs run in, when the game has one.
    /// </summary>
    string? PrefixPath { get; }

    /// <summary>
    /// The name of the strategy that would run the program, set when <see cref="CanRunWindowsProgram"/> is true.
    /// </summary>
    string? Strategy { get; }

    /// <summary>
    /// The outcome of the last run, empty before the first.
    /// </summary>
    string? LastRunMessage { get; }
}
