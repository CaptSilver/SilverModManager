using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// The command line that would run a Windows program for a game, or the reason there is none.
/// </summary>
[PublicAPI]
public sealed record ToolRunPlan
{
    /// <summary>
    /// The name of the strategy that built the plan, set when <see cref="IsRunnable"/>.
    /// </summary>
    public string? Strategy { get; private init; }

    /// <summary>
    /// The program followed by its arguments, empty unless <see cref="IsRunnable"/>.
    /// </summary>
    public IReadOnlyList<string> Argv { get; private init; } = [];

    /// <summary>
    /// The directory the program starts in.
    /// </summary>
    public AbsolutePath? WorkingDirectory { get; private init; }

    /// <summary>
    /// The variables to set on top of the inherited environment.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; private init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Why the strategy that normally runs this game was passed over for a last-resort one, as a full phrase
    /// ("SteamProton cannot run: …"). Null when the regular strategy runs.
    /// </summary>
    public string? FallbackReason { get; private init; }

    /// <summary>
    /// Why the program cannot be run, set when <see cref="IsRunnable"/> is not.
    /// </summary>
    public string? Diagnostic { get; private init; }

    /// <summary>
    /// Whether the plan failed because the strategy needs an x86-64 machine and this one cannot run x86-64
    /// programs.
    /// </summary>
    public bool CannotRunX86_64 { get; private init; }

    /// <summary>
    /// Whether the plan can be run.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Strategy))]
    [MemberNotNullWhen(true, nameof(WorkingDirectory))]
    [MemberNotNullWhen(false, nameof(Diagnostic))]
    public bool IsRunnable => Diagnostic is null;

    /// <summary/>
    public static ToolRunPlan Runnable(
        string strategy,
        IReadOnlyList<string> argv,
        AbsolutePath workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        string? fallbackReason = null)
        => new() { Strategy = strategy, Argv = argv, WorkingDirectory = workingDirectory, Environment = environment, FallbackReason = fallbackReason };

    /// <summary/>
    public static ToolRunPlan Failed(string diagnostic, bool cannotRunX86_64 = false)
        => new() { Diagnostic = diagnostic, CannotRunX86_64 = cannotRunX86_64 };
}

/// <summary>
/// What a finished tool run reports.
/// </summary>
/// <param name="ExitCode">The exit code of the program that was started.</param>
/// <param name="Duration">How long the program ran.</param>
/// <param name="Strategy">The name of the strategy that ran it.</param>
/// <param name="FallbackReason">Why the last-resort strategy was used, or null when the regular one ran.</param>
/// <param name="StdoutTail">The end of the program's standard output, from the first complete line kept.</param>
/// <param name="StderrTail">The end of the program's standard error, from the first complete line kept.</param>
[PublicAPI]
public sealed record ToolRunResult(
    int ExitCode,
    TimeSpan Duration = default,
    string Strategy = "",
    string? FallbackReason = null,
    string StdoutTail = "",
    string StderrTail = "");
