using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// Runs Windows programs inside the Wine prefix a game lives in, the way that game's launcher would.
/// </summary>
[PublicAPI]
public interface IToolRunner
{
    /// <summary>
    /// Builds the command line for running <paramref name="exe"/> with the game's prefix and runner. Host
    /// paths in <paramref name="args"/> are rewritten to Windows paths. When nothing can run the program the
    /// plan carries the reason instead. A <paramref name="via"/> other than <see cref="ToolRunVia.Auto"/> forces
    /// that strategy over the one the prefix would pick, and is a diagnostic when it cannot run.
    /// </summary>
    ToolRunPlan Plan(
        GameInstallation installation,
        AbsolutePath exe,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env,
        ToolRunVia via = ToolRunVia.Auto);

    /// <summary>
    /// Runs the plan from <see cref="Plan"/> and waits for the program to exit.
    /// </summary>
    /// <exception cref="InvalidOperationException">No strategy can run the program; the message is the plan's diagnostic.</exception>
    Task<ToolRunResult> RunWindowsTool(
        GameInstallation installation,
        AbsolutePath exe,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? env,
        bool logOutput,
        CancellationToken cancellationToken,
        ToolRunVia via = ToolRunVia.Auto);

    /// <summary>
    /// Runs a Wine verb or an installer inside the game's prefix with the runner the prefix would pick, waits
    /// for it to exit and returns what it wrote. A non-zero exit code is a result; only a program that cannot
    /// be started at all throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">No strategy can run the command; the message is the reason.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the process is interrupted, then killed.</exception>
    Task<PrefixRunResult> RunInPrefix(
        GameInstallation installation,
        PrefixCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether a program can be run for this installation, and if not, why.
    /// </summary>
    bool CanRun(GameInstallation installation, [NotNullWhen(false)] out string? diagnostic);

    /// <summary>
    /// Whether a program can be run for this installation.
    /// </summary>
    bool CanRun(GameInstallation installation) => CanRun(installation, out _);
}
