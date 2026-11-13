using System.ComponentModel;
using CliWrap;
using SilverModManager.Sdk;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

/// <summary>
/// Answers a version probe with a chosen output, so no binary is ever started. Like a real exec, it fails with
/// "permission denied" when the binary has no executable bit.
/// </summary>
public sealed class FakeVersionRunner : IProcessRunner
{
    public string Output { get; set; } = "me3 0.13.0";

    public void Run(Command command, bool logOutput = false) => throw new NotSupportedException();
    public Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public async Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default)
    {
        if (!File.GetUnixFileMode(command.TargetFilePath).HasFlag(UnixFileMode.UserExecute))
            throw new Win32Exception(13, $"An error occurred trying to start process '{command.TargetFilePath}'. Permission denied");

        await command.StandardOutputPipe.CopyFromAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Output)), cancellationToken);
        return new CommandResult(0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }
}
