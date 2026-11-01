using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Tools;

/// <summary>
/// Runs a <see cref="ToolPipeline"/>: each step is the catalogue tool the user has added under that id, run through
/// the tool manager like any other tool, and the first failure ends the pipeline.
/// </summary>
public sealed class ToolPipelineRunner(IToolManager tools, IEnumerable<ToolPipeline> pipelines)
{
    private readonly ToolPipeline[] _pipelines = pipelines.ToArray();

    public IReadOnlyList<ToolPipeline> Pipelines => _pipelines;

    public ToolPipeline? Find(string name)
        => _pipelines.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs the steps in order. <paramref name="report"/> gets each step's finished line as it ends. Returns the
    /// loadout after the last step.
    /// </summary>
    /// <exception cref="ToolRefusedException">A step's tool has not been added; nothing was run.</exception>
    public async Task<Loadout.ReadOnly> Run(ToolPipeline pipeline, Loadout.ReadOnly loadout, IJobMonitor monitor, Action<string> report, CancellationToken token)
    {
        var available = tools.GetTools(loadout).OfType<ModdingTool>().ToArray();
        var steps = new List<ModdingTool>();
        var missing = new List<string>();
        foreach (var id in pipeline.Steps)
        {
            var tool = available.FirstOrDefault(t => t.CatalogueId == id);
            if (tool is null) missing.Add(id.Value);
            else steps.Add(tool);
        }

        if (missing.Count > 0)
            throw new ToolRefusedException($"'{pipeline.Name}' needs {string.Join(", ", missing)}, which {(missing.Count == 1 ? "has" : "have")} not been added for this game. Add it first; nothing was run.");

        foreach (var step in steps)
        {
            var outcome = await tools.RunToolWithOutcome(step, loadout, monitor, token);
            loadout = outcome.Loadout;
            report(ToolRunMessage.Finished(step.Name, outcome.ExitCode, outcome.OutputGroup, outcome.CapturedFiles));
        }

        return loadout;
    }
}
