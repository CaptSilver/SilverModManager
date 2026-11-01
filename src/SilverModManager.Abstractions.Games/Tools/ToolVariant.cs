namespace SilverModManager.Abstractions.Games.Tools;

/// <summary>
/// The same program run for a narrower job, with its own arguments and its own idea of a failed run. xEdit's Quick
/// Auto Clean is one: it is the editor started to clean a single plugin and close again.
/// </summary>
/// <param name="Name">Shown beside the tool's name, as in "xEdit: Quick Auto Clean".</param>
/// <param name="Arguments">Replaces the definition's arguments. May name <see cref="ToolVariableNames.Plugin"/>.</param>
/// <param name="ExitCodes">Replaces the definition's exit policy.</param>
public sealed record ToolVariant(string Name, ArgumentTemplate Arguments, ExitCodePolicy ExitCodes)
{
    /// <summary>The definition this variant runs as: everything else about the tool stays as it is.</summary>
    public ToolDefinition Apply(ToolDefinition definition) => definition with
    {
        DisplayName = $"{definition.DisplayName}: {Name}",
        Arguments = Arguments,
        ExitCodes = ExitCodes,
        QuickAutoClean = null,
    };
}
