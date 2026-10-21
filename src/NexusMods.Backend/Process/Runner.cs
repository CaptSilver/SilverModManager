using System.Diagnostics;
using CliWrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.CrossPlatform;
using NexusMods.Paths;
using NexusMods.Paths.Utilities;
using NexusMods.Sdk;
using NexusMods.Sdk.Settings;

namespace NexusMods.Backend.Process;

internal class ProcessRunner : IProcessRunner
{
    private readonly ILogger _logger;
    private readonly IFileSystem _fileSystem;
    private readonly AbsolutePath _processLogsFolder;

    /// <summary>
    /// How long a cancelled command gets to react to an interrupt before its process tree is killed.
    /// </summary>
    /// <remarks>
    /// The interrupt comes first because of host shims. Inside a distrobox, <c>flatpak</c> is a shell script
    /// that forks <c>host-spawn</c>, which passes an interrupt on to the real process on the host but cannot
    /// pass on a kill: a killed <c>host-spawn</c> leaves the host process running under
    /// <c>flatpak-session-helper</c>, which is how abandoned <c>flatpak run</c> probes piled up. The script
    /// holds an interrupt until its child exits, so the interrupt goes to every process in the tree.
    /// </remarks>
    internal TimeSpan KillGracePeriod { get; init; } = TimeSpan.FromSeconds(3);

    public ProcessRunner(IServiceProvider serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILogger<ProcessRunner>>();
        _fileSystem = serviceProvider.GetRequiredService<IFileSystem>();

        // TODO: rework
        var settingsManager = serviceProvider.GetRequiredService<ISettingsManager>();
        _processLogsFolder = LoggingSettings.GetLogBaseFolder(_fileSystem).Combine("ProcessLogs");
        _logger.LogInformation("Using process log folder at {Path}", _processLogsFolder);

        _processLogsFolder.CreateDirectory();
    }

    private string GetFileName(Command command)
    {
        return PathHelpers.IsRooted(command.TargetFilePath)
            ? _fileSystem.FromUnsanitizedFullPath(command.TargetFilePath).FileName
            : RelativePath.FromUnsanitizedInput(command.TargetFilePath).FileName.ToString();
    }

    private static string GetLogFileName(string fileName)
    {
        // TODO: consider smaller IDs for shorted file names
        var id = Guid.NewGuid();
        return $"{fileName}-{id:D}";
    }

    public void Run(Command command, bool logOutput)
    {
        var task = ExecuteCommand(command, logOutput, cancellationToken: CancellationToken.None);
        task.FireAndForget(_logger, cancellationToken: CancellationToken.None);
    }

    public Task<CommandResult> RunAsync(Command command, bool logOutput, CancellationToken cancellationToken = default)
    {
        command = SetupLogging(command, logOutput);
        return ExecuteCommand(command, logOutput, cancellationToken);
    }

    private async Task<CommandResult> ExecuteCommand(Command command, bool logOutput, CancellationToken cancellationToken)
    {
        if (logOutput) _logger.LogDebug("Starting command `{Command}`", command.ToString());

        var sw = Stopwatch.StartNew();

        // CliWrap's graceful token sends SIGINT to the process alone and keeps waiting; its forceful token kills
        // the process tree. The interrupt for the rest of the tree is sent here.
        using var forceful = new CancellationTokenSource();
        var execution = command.ExecuteAsync(forcefulCancellationToken: forceful.Token, gracefulCancellationToken: cancellationToken);
        await using var interrupt = cancellationToken.Register(() => InterruptProcessTree(command, execution.ProcessId, sw.Elapsed, forceful));
        await using var kill = forceful.Token.Register(() => _logger.LogWarning("Killing `{Command}`: still running {GracePeriod} after the interrupt", command.ToString(), KillGracePeriod));
        var result = await execution;
        sw.Stop();

        if (!logOutput) return result;
        _logger.LogDebug("Command `{Command}` finished after {RunTime} seconds with exit Code {ExitCode}", command.ToString(), result.RunTime.TotalSeconds, result.ExitCode);
        return result;
    }

    private void InterruptProcessTree(Command command, int pid, TimeSpan elapsed, CancellationTokenSource forceful)
    {
        _logger.LogWarning("Interrupting `{Command}` (pid {Pid}) after {Elapsed}: the run was cancelled", command.ToString(), pid, elapsed);
        try
        {
            ProcessTree.Interrupt(ProcessTree.Descendants(pid));
        }
        catch (Exception e)
        {
            // The kill after the grace period still ends the tree, so this is no reason to stop.
            _logger.LogError(e, "Failed to interrupt the children of `{Command}` (pid {Pid})", command.ToString(), pid);
        }
        forceful.CancelAfter(KillGracePeriod);
    }

    private Command SetupLogging(Command command, bool logOutput)
    {
        var stdInPipe = command.StandardInputPipe == PipeSource.Null ? PipeSource.Null : command.StandardInputPipe;

        if (!logOutput)
        {
            // We require a non-null pipe here, for more details, see:
            // https://github.com/Nexus-Mods/NexusMods.App/issues/1905#issuecomment-2302503110
            // https://github.com/Nexus-Mods/NexusMods.App/issues/1905#issuecomment-2302486535
            command = command.WithStandardInputPipe(stdInPipe);
            if (command.StandardOutputPipe == PipeTarget.Null) command = command.WithStandardOutputPipe(PipeTarget.ToStream(Stream.Null));
            if (command.StandardOutputPipe == PipeTarget.Null) command = command.WithStandardErrorPipe(PipeTarget.ToStream(Stream.Null));
            return command;
        }

        var fileName = GetFileName(command);
        var logFileName = GetLogFileName(fileName);
        var stdOutFilePath = _processLogsFolder.Combine(logFileName + ".stdout.log");
        var stdErrFilePath = _processLogsFolder.Combine(logFileName + ".stderr.log");

        var stdOutPipe = PipeTarget.Create(async (stdOut, cancellationToken) =>
        {
            await using var fileStream = stdOutFilePath.Open(FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            await stdOut.CopyToAsync(fileStream, cancellationToken: cancellationToken);
        });

        var stdErrPipe = PipeTarget.Create(async (stdOut, cancellationToken) =>
        {
            await using var fileStream = stdErrFilePath.Open(FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            await stdOut.CopyToAsync(fileStream, cancellationToken: cancellationToken);
        });

        var mergedStdOutPipe = command.StandardOutputPipe == PipeTarget.Null ? stdOutPipe : PipeTarget.Merge(command.StandardOutputPipe, stdOutPipe);
        var mergedStdErrPipe = command.StandardErrorPipe == PipeTarget.Null ? stdErrPipe : PipeTarget.Merge(command.StandardErrorPipe, stdErrPipe);

        _logger.LogInformation("Setup process logs {StdOutLogPath} and {StdErrLogPath}", stdOutFilePath, stdErrFilePath);
        return command
            .WithStandardInputPipe(stdInPipe)
            .WithStandardOutputPipe(mergedStdOutPipe)
            .WithStandardErrorPipe(mergedStdErrPipe);
    }

    public Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource();

        process.EnableRaisingEvents = true;
        var hasExited = false;

        process.Exited += (_, _) =>
        {
            hasExited = true;
            tcs.SetResult();
            process.Dispose();
        };
        
        cancellationToken.Register(() =>
        {
            if (hasExited) return;
            try
            {
                _logger.LogInformation("Killing process `{Process}`", process.StartInfo.FileName);
                process.Kill();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to kill process `{Process}`", process.StartInfo.FileName);
                tcs.SetException(e);
            }
        });

        try
        {
            _logger.LogInformation("Executing process `{Process}`", process.StartInfo.FileName);
            process.Start();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to start process `{Process}`", process.StartInfo.FileName);
            tcs.SetException(e);
        }

        return tcs.Task; 
    }
}
