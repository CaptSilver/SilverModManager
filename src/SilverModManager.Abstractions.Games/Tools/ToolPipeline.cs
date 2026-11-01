using Microsoft.Extensions.DependencyInjection;

namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>
/// A named, ordered list of catalogue tools run one after the other against the same loadout. The steps are ids,
/// not tools: each is resolved from the tools the user has added when the pipeline runs.
/// </summary>
public sealed record ToolPipeline(string Name, IReadOnlyList<ToolId> Steps);

public static class ToolPipelineServices
{
    /// <summary>Adds a pipeline the Tools page and the <c>run-pipeline</c> command offer.</summary>
    public static IServiceCollection AddToolPipeline(this IServiceCollection services, ToolPipeline pipeline)
        => services.AddSingleton(pipeline);
}
