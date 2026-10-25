using System.ComponentModel;
using CliWrap;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk;

namespace SilverModManager.Games.FromSoft.Cleanup;

/// <summary>How an attempt to run a privileged command ended.</summary>
public enum ElevatedOutcome
{
    Succeeded,

    /// <summary>The user dismissed the password dialog.</summary>
    Cancelled,

    /// <summary>The command could not run here (no pkexec, no polkit agent, not authorised), so the user has to run it.</summary>
    Unavailable,
}

/// <param name="Outcome">How the attempt ended.</param>
/// <param name="Detail">What went wrong, for the log and the fallback message; null on success or cancel.</param>
public sealed record ElevatedResult(ElevatedOutcome Outcome, string? Detail = null);

/// <summary>
/// Runs <c>chattr -i</c> on a file of the game folder through <c>pkexec</c>. It is the only way the app asks for
/// root, so it refuses every other program, flag or path before anything starts.
/// </summary>
public sealed class ElevatedCommandRunner(IProcessRunner processRunner, ILogger<ElevatedCommandRunner> logger)
{
    // pkexec runs the absolute path: polkit matches the program it was asked to run, and a PATH lookup as root is
    // not something to leave to the environment.
    internal const string ChattrPath = "/usr/bin/chattr";

    private const int DialogDismissed = 126;

    public async Task<ElevatedResult> RunAsync(string gameFolder, PrivilegedCommand command, CancellationToken cancellationToken = default)
    {
        Validate(gameFolder, command);

        var pkexec = Cli.Wrap("pkexec")
            .WithArguments([ChattrPath, PrivilegedCommand.ClearImmutableFlag, command.TargetPath])
            .WithValidation(CommandResultValidation.None);

        try
        {
            var result = await processRunner.RunAsync(pkexec, cancellationToken: cancellationToken);
            return result.ExitCode switch
            {
                0 => new ElevatedResult(ElevatedOutcome.Succeeded),
                DialogDismissed => new ElevatedResult(ElevatedOutcome.Cancelled),
                127 => new ElevatedResult(ElevatedOutcome.Unavailable, "pkexec is not authorised here or no polkit agent is running"),
                var code => new ElevatedResult(ElevatedOutcome.Unavailable, $"pkexec exited with code {code}"),
            };
        }
        catch (Win32Exception e)
        {
            logger.LogWarning(e, "pkexec could not be started");
            return new ElevatedResult(ElevatedOutcome.Unavailable, "pkexec is not installed");
        }
    }

    private static void Validate(string gameFolder, PrivilegedCommand command)
    {
        var argv = command.Argv;
        if (argv.Length != 3 || argv[0] != PrivilegedCommand.ChattrProgram || argv[1] != PrivilegedCommand.ClearImmutableFlag)
            throw new ArgumentException($"Only '{PrivilegedCommand.ChattrProgram} {PrivilegedCommand.ClearImmutableFlag} <file>' may run with elevated rights, not '{string.Join(' ', argv)}'.", nameof(command));

        var path = argv[2];
        var folder = Path.GetFullPath(gameFolder).TrimEnd('/') + "/";
        if (!Path.IsPathRooted(path) || Path.GetFullPath(path) != path || !path.StartsWith(folder, StringComparison.Ordinal))
            throw new ArgumentException($"'{path}' is not a normalised path inside the game folder {gameFolder}.", nameof(command));

        // chattr follows links, so a link anywhere below the game folder (the file or a folder on the way to it)
        // would reach a file outside it.
        var current = folder.TrimEnd('/');
        foreach (var segment in path[folder.Length..].Split('/'))
        {
            current = $"{current}/{segment}";
            if (new FileInfo(current).LinkTarget is not null)
                throw new ArgumentException($"'{path}' goes through the link {current}, so the file it reaches could lie outside the game folder.", nameof(command));
        }
    }
}
