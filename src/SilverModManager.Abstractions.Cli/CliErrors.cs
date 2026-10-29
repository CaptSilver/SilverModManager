using NexusMods.Paths;
using SilverModManager.CrossPlatform;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Cli;

/// <summary>
/// How a failed command is put in front of the person at the terminal. A refusal is shown as its own sentence; a
/// crash is shown as a pointer to the log. Neither ever prints a stack trace.
/// </summary>
public static class CliErrors
{
    /// <summary>
    /// The exit code of a command that was refused.
    /// </summary>
    public const int RefusalExitCode = 1;

    /// <summary>
    /// The exit code of a command that crashed.
    /// </summary>
    public const int CrashExitCode = -1;

    /// <summary>
    /// The sentence to print for <paramref name="exception"/>: the refusal's message followed by its command line hint, or where to find the details of a crash.
    /// </summary>
    public static string Describe(Exception exception) => UserFacingErrors.Find(exception) switch
    {
        null => CrashPointer,
        { CommandLineHint: { } hint } refusal => $"{refusal.Message}{Environment.NewLine}{Environment.NewLine}{hint}",
        var refusal => refusal.Message,
    };

    /// <summary>
    /// The exit code for <paramref name="exception"/>: <see cref="RefusalExitCode"/> or <see cref="CrashExitCode"/>.
    /// </summary>
    public static int ExitCode(Exception exception) =>
        UserFacingErrors.Find(exception) is null ? CrashExitCode : RefusalExitCode;

    /// <summary>
    /// What a crash prints in place of its exception.
    /// </summary>
    public static string CrashPointer =>
        $"Something went wrong. Details are in the log at {LoggingSettings.GetLogBaseFolder(FileSystem.Shared)}.";
}
