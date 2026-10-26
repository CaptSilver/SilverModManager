namespace SilverModManager.App.Mcp;

/// <summary>Builds a tool that runs one verb in the app.</summary>
internal static class VerbTool
{
    public static readonly ToolParam LoadoutParam = new(
        "loadout", "The loadout: its hex id, or <game>/<shortName>. Use loadouts_list to find it.", Required: true);

    /// <param name="verb">The verb and its sub-commands, as typed on the command line.</param>
    /// <param name="takesLoadout">Whether the verb gets <c>--loadout</c> from the tool's <c>loadout</c> argument.</param>
    /// <param name="flags">Fixed flags that go last, so nothing before them can cancel them out.</param>
    /// <param name="emptyText">What to answer when the verb succeeds without printing anything.</param>
    public static McpTool Create(
        McpToolContext context, string name, string description, bool readOnly, string[] verb, bool takesLoadout,
        TimeSpan timeout, string[]? flags = null, bool reportsExitOne = false, Func<ToolArgs, string>? emptyText = null)
    {
        IReadOnlyList<ToolParam> parameters = takesLoadout ? [LoadoutParam] : [];
        return new McpTool(name, description, parameters, readOnly, (args, token) =>
        {
            string[] argv = [.. verb, .. takesLoadout ? new[] { "--loadout", args.RequiredString("loadout") } : [], .. flags ?? []];
            return context.Verbs.RunAsync(argv, new VerbCall(timeout, reportsExitOne, context.Limits.MaxOutputChars, emptyText?.Invoke(args)), token);
        });
    }
}
