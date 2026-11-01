using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Platform.Proton.Tools;
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
    /// The catalogue tools the user has added for this game, each with what its Wine prefix still lacks.
    /// </summary>
    IReadOnlyList<CatalogueToolRow> CatalogueTools { get; }

    /// <summary>
    /// The plugin file names Quick Auto Clean can be pointed at; empty when the game offers none.
    /// </summary>
    IReadOnlyList<string> Plugins { get; }

    /// <summary>
    /// The pipelines on offer, such as LOD generation.
    /// </summary>
    IReadOnlyList<ToolPipeline> Pipelines { get; }

    /// <summary>
    /// Cleans one plugin with a tool's Quick Auto Clean mode.
    /// </summary>
    ReactiveCommand<QuickAutoCleanRequest> QuickAutoCleanCommand { get; }

    /// <summary>
    /// Runs a pipeline's tools one after the other.
    /// </summary>
    ReactiveCommand<ToolPipeline> RunPipelineCommand { get; }

    /// <summary>
    /// Adds the tools from a picked folder (true) or a picked program (false) to this game.
    /// </summary>
    ReactiveCommand<bool> AddToolCommand { get; }

    /// <summary>
    /// Stops the tool or pipeline that is running.
    /// </summary>
    ReactiveCommand<Unit> CancelRunCommand { get; }

    /// <summary>
    /// Whether a tool or pipeline is running now.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// "Running DynDOLOD for 12 min" while something runs, otherwise empty.
    /// </summary>
    string? RunningMessage { get; }

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

/// <summary>
/// A catalogue tool on the page: the tool, the saved row it came from, and what its prefix still lacks.
/// </summary>
/// <param name="Row">The saved row, for removing the tool; null when it cannot be matched to one.</param>
/// <param name="MissingPrerequisites">Display names of the prefix components the tool needs and lacks.</param>
public sealed record CatalogueToolRow(string Name, ModdingTool Tool, UserTool.ReadOnly? Row, IReadOnlyList<string> MissingPrerequisites, bool CanQuickAutoClean)
{
    /// <summary>What to tell the user about the missing components, or null when the tool is ready.</summary>
    public string? PrerequisiteMessage => MissingPrerequisites.Count == 0
        ? null
        : $"Needs {string.Join(" and ", MissingPrerequisites)} in the game's Wine prefix. Set it up in the prefix before running.";

    internal static CatalogueToolRow For(ModdingTool tool, GameInstallation installation, IEnumerable<UserTool.ReadOnly> rows)
    {
        UserTool.ReadOnly? row = null;
        foreach (var candidate in rows)
        {
            if (candidate.ToolId.HasValue && candidate.ToolId.Value == tool.CatalogueId?.Value && candidate.Exe == tool.Exe.GetFullPath())
            {
                row = candidate;
                break;
            }
        }

        return new CatalogueToolRow(tool.Name, tool, row, tool.MissingPrerequisites(installation), tool.CanQuickAutoClean);
    }
}

/// <summary>A plugin picked for a tool's Quick Auto Clean.</summary>
public sealed record QuickAutoCleanRequest(ModdingTool Tool, string Plugin);
