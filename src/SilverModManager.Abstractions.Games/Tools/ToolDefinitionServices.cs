using Microsoft.Extensions.DependencyInjection;

namespace SilverModManager.Abstractions.Games.Tools;

public static class ToolDefinitionServices
{
    /// <summary>
    /// Adds a catalogue tool. The detector and the catalogue provider read every definition registered this way as
    /// an <c>IEnumerable&lt;ToolDefinition&gt;</c>, so each game family registers the tools it knows about.
    /// </summary>
    public static IServiceCollection AddToolDefinition(this IServiceCollection services, ToolDefinition definition)
        => services.AddSingleton(definition);
}
