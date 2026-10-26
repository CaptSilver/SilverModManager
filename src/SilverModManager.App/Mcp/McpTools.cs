namespace SilverModManager.App.Mcp;

/// <summary>How long each kind of tool waits for the app, and how much text a result may hold.</summary>
/// <param name="ReadTimeout">Verbs that read what the app already knows.</param>
/// <param name="SlowReadTimeout">Verbs that read game files or hash them.</param>
/// <param name="ApplyTimeout">Verbs that change the game folder.</param>
/// <param name="MaxOutputChars">Longer results are cut, because the client truncates large tool output anyway.</param>
public sealed record McpLimits(TimeSpan ReadTimeout, TimeSpan SlowReadTimeout, TimeSpan ApplyTimeout, int MaxOutputChars)
{
    public static McpLimits Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120), TimeSpan.FromMinutes(20), 60_000);
}

/// <summary>What the tools need from the process they run in.</summary>
/// <param name="Verbs">Runs verbs in the running window.</param>
/// <param name="MainLogPath">The log file of the window, or null when it is not known.</param>
/// <param name="RuntimeDir"><c>XDG_RUNTIME_DIR</c>, where launch locks live.</param>
/// <param name="MountInfoPath">The mount table the launch status report reads.</param>
public sealed record McpToolContext(VerbRunner Verbs, McpLimits Limits, string? MainLogPath, string? RuntimeDir, string MountInfoPath = "/proc/self/mountinfo");

/// <summary>The tools a server offers. A read-only server gets the ones that cannot change anything.</summary>
public static class McpToolTable
{
    public static IReadOnlyList<McpTool> Create(McpToolContext context, bool readOnly) =>
        readOnly ? ReadOnlyTools.Create(context) : [.. ReadOnlyTools.Create(context), .. MutatingTools.Create(context)];
}
