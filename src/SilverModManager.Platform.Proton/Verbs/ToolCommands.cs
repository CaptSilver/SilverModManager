using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Platform.Proton.UserTools;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Platform.Proton.Verbs;

/// <summary>
/// The command-line side of the Tools page: add a tool, run one, run a pipeline. Every outcome is one plain line;
/// a refusal shows its own message and anything else points at the log.
/// </summary>
public sealed class ToolCommands(ToolInstaller installer, IToolManager tools, ToolPipelineRunner pipelines, IJobMonitor monitor, ILogger<ToolCommands> logger)
{
    public const int FailureExitCode = 1;

    public Task<int> AddAsync(IRenderer renderer, Loadout.ReadOnly loadout, string path)
        => Guarded(renderer, "Adding the tool", async () =>
        {
            if (System.IO.Path.IsPathRooted(path) is false)
                throw new ToolRefusedException($"'{path}' is not an absolute path. Give the full path of a tool's folder or program.");

            var result = await installer.Add(loadout, FileSystem.Shared.FromUnsanitizedFullPath(path));
            foreach (var name in result.Added) await renderer.TextLine($"Added {name}.");
            foreach (var name in result.AlreadyAdded) await renderer.TextLine($"{name} is already added.");
        });

    public Task<int> RunToolAsync(IRenderer renderer, Loadout.ReadOnly loadout, string toolName, string? cleanPlugin, CancellationToken token)
        => Guarded(renderer, "Running the tool", async () =>
        {
            var added = tools.GetTools(loadout).OfType<ModdingTool>().ToArray();
            var tool = added.FirstOrDefault(t => string.Equals(t.Name, toolName.Trim(), StringComparison.OrdinalIgnoreCase)
                                                 || string.Equals(t.CatalogueId?.Value, toolName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (tool is null)
            {
                var names = added.Length == 0 ? "none added yet, use add-tool" : string.Join(", ", added.Select(t => t.Name));
                throw new ToolRefusedException($"No tool called '{toolName}' is added for loadout '{loadout.Name}'. Added tools: {names}.");
            }

            ITool toRun = tool;
            if (cleanPlugin is not null)
                toRun = tool.ForQuickAutoClean(cleanPlugin) ?? throw new ToolRefusedException($"{tool.Name} cannot clean a plugin.");

            var outcome = await tools.RunToolWithOutcome(toRun, loadout, monitor, token);
            await renderer.TextLine(ToolRunMessage.Finished(toRun.Name, outcome.ExitCode, outcome.OutputGroup, outcome.CapturedFiles));
        });

    public Task<int> RunPipelineAsync(IRenderer renderer, Loadout.ReadOnly loadout, string pipelineName, CancellationToken token)
        => Guarded(renderer, "Running the pipeline", async () =>
        {
            var pipeline = pipelines.Find(pipelineName, loadout)
                           ?? throw new ToolRefusedException($"No pipeline called '{pipelineName}'. Pipelines: {string.Join(", ", pipelines.PipelinesFor(loadout).Select(p => p.Name))}.");

            var lines = new List<string>();
            try
            {
                await pipelines.Run(pipeline, loadout, monitor, lines.Add, token);
            }
            finally
            {
                foreach (var line in lines) await renderer.TextLine(line);
            }
        });

    // A tool's own refusals (a missing prefix component, a non-zero exit) are plain InvalidOperationExceptions with
    // a message written for the user; anything else is a crash and keeps its detail in the log.
    private async Task<int> Guarded(IRenderer renderer, string what, Func<Task> action)
    {
        try
        {
            await action();
            return 0;
        }
        catch (OperationCanceledException)
        {
            await renderer.TextLine($"{what} was cancelled.");
        }
        catch (Exception ex) when (ex is UserFacingException or InvalidOperationException)
        {
            await renderer.TextLine(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogFailure(ex, "{What} failed", what);
            await renderer.TextLine($"{what} failed. The SilverModManager log has the details.");
        }

        return FailureExitCode;
    }
}
