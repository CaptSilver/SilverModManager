using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.ToolRunner;

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
    /// The setting that forces this strategy, or null when it is only ever picked from the prefix kind.
    /// </summary>
    ToolRunVia? Via => null;

    /// <summary>
    /// The reason a program cannot be run for the installation, or null when it can.
    /// </summary>
    string? CheckRunnable(GameInstallation installation, ResolvedPrefix prefix);

    /// <summary>
    /// Builds the plan for running <paramref name="command"/>, which is an executable or a Wine verb.
    /// </summary>
    ToolRunPlan Plan(
        GameInstallation installation,
        ResolvedPrefix prefix,
        PrefixCommand command);

    /// <summary>
    /// Builds the plan for <paramref name="exe"/>.
    /// </summary>
    ToolRunPlan Plan(
        GameInstallation installation,
        ResolvedPrefix prefix,
        AbsolutePath exe,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env)
        => Plan(installation, prefix, PrefixCommand.ForExe(exe, args, env));
}
