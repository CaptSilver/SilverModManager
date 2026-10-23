using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using NexusMods.Paths;

namespace NexusMods.Sdk.Games;

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
    /// Why the program cannot be run, set when <see cref="IsRunnable"/> is not.
    /// </summary>
    public string? Diagnostic { get; private init; }

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
        IReadOnlyDictionary<string, string> environment)
        => new() { Strategy = strategy, Argv = argv, WorkingDirectory = workingDirectory, Environment = environment };

    /// <summary/>
    public static ToolRunPlan Failed(string diagnostic) => new() { Diagnostic = diagnostic };
}

/// <summary>
/// What a finished tool run reports.
/// </summary>
/// <param name="ExitCode">The exit code of the program that was started.</param>
[PublicAPI]
public sealed record ToolRunResult(int ExitCode);
