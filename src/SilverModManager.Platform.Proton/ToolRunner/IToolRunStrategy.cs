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
    /// What about the launcher this strategy starts needs an x86-64 host, as a phrase that fits after "and" and
    /// ends with what the user can pick instead when there is something to pick; or null when the launcher runs
    /// on an aarch64 host's own CPU. The runner only honours null on an Arm64 process and never asks on an x86-64
    /// one. Asked after <see cref="CheckRunnable"/> has passed, so a strategy may assume its launcher resolves.
    /// </summary>
    string? X86_64Requirement(GameInstallation installation, ResolvedPrefix prefix);

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
