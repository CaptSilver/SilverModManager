using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Games;
using R3;

namespace SilverModManager.App.UI.Pages.Tools;

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
    /// The programs the user added for this loadout's installation. They are also in <see cref="Tools"/>, where
    /// they are run.
    /// </summary>
    IReadOnlyList<UserTool.ReadOnly> UserTools { get; }

    /// <summary>
    /// Persists a program for this loadout's installation.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or the program is not an absolute path.</exception>
    Task AddUserTool(UserToolDraft draft);

    /// <summary>
    /// Replaces the name, program, arguments and run-via setting of an added program.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The name is empty, the program is not an absolute path, or the tool was removed.
    /// </exception>
    Task UpdateUserTool(UserTool.ReadOnly tool, UserToolDraft draft);

    /// <summary>
    /// Deletes an added program.
    /// </summary>
    Task RemoveUserTool(UserTool.ReadOnly tool);

    /// <summary>
    /// The outcome of the last run, empty before the first.
    /// </summary>
    string? LastRunMessage { get; }
}

/// <summary>
/// What the user fills in to add or change a program.
/// </summary>
/// <param name="Name">Shown in the tools list.</param>
/// <param name="Exe">Absolute host path of the program.</param>
/// <param name="Arguments">Arguments as typed; spaces separate and double quotes group.</param>
/// <param name="RunVia">How to start the program.</param>
public sealed record UserToolDraft(string Name, string Exe, string Arguments, ToolRunVia RunVia);
