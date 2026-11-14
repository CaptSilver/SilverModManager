using CliWrap;
using JetBrains.Annotations;

namespace SilverModManager.Sdk;

/// <summary>
/// Process abstraction.
/// </summary>
[PublicAPI]
public interface IProcessRunner
{
    /// <summary>
    /// Runs a command.
    /// </summary>
    /// <remarks>
    /// This method exists immediately and doesn't wait for the process to exit.
    /// </remarks>
    void Run(Command command, bool logOutput = false);

    /// <summary>
    /// Runs a command.
    /// </summary>
    /// <param name="command">The command to run.</param>
    /// <param name="logOutput">Whether the command's output is written to the log.</param>
    /// <param name="cancellationToken">Interrupts the process tree, then kills it.</param>
    /// <param name="started">Called with the child's pid right after it starts, so a caller can tell this process from the ones it did not start.</param>
    /// <returns>A task that completes when the spawned process exits.</returns>
    Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default, Action<int>? started = null);

    /// <summary>
    /// Runs a process.
    /// </summary>
    /// <returns>A task that completes when the spawned process exists.</returns>
    Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default);
}
