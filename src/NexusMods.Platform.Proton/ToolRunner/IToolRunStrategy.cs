using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.ToolRunner;

/// <summary>
/// Knows how to run a Windows program for prefixes of one <see cref="CompatibilityLayerKind"/>.
/// </summary>
public interface IToolRunStrategy
{
    /// <summary>
    /// The prefix kind this strategy handles.
    /// </summary>
    CompatibilityLayerKind Kind { get; }

    /// <summary>
    /// The reason a program cannot be run for the installation, or null when it can.
    /// </summary>
    string? CheckRunnable(GameInstallation installation, ResolvedPrefix prefix);

    /// <summary>
    /// Builds the plan for <paramref name="exe"/>.
    /// </summary>
    ToolRunPlan Plan(
        GameInstallation installation,
        ResolvedPrefix prefix,
        AbsolutePath exe,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env);
}
