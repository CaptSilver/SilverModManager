using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SilverModManager.Platform.Proton.Selftest;

/// <summary>
/// A child process the selftest talks to line by line and may signal. Everything that waits has a timeout, so
/// a helper that hangs becomes a failed check instead of a hung selftest.
/// </summary>
internal sealed partial class HelperProcess : IDisposable
{
    private const int SigTerm = 15;
    private const int NoSuchProcess = 3;

    private readonly Process _process;
    private readonly Task<string> _error;

    private HelperProcess(Process process)
    {
        _process = process;
        // Drained from the start so a chatty helper can't block on a full pipe.
        _error = process.StandardError.ReadToEndAsync();
    }

    /// <summary>
    /// Starts <paramref name="file"/>. Throws <see cref="System.ComponentModel.Win32Exception"/> when it can't be started.
    /// </summary>
    public static HelperProcess Start(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return new HelperProcess(Process.Start(info) ?? throw new InvalidOperationException($"Failed to start {file}"));
    }

    /// <summary>
    /// The next line of output, or null when the helper closed its output or nothing came within the timeout.
    /// </summary>
    public async Task<string?> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask().WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The exit code, or null when the helper is still running after the timeout.
    /// </summary>
    public async Task<int?> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
            return _process.ExitCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// What the helper wrote to stderr, trimmed, once it has exited.
    /// </summary>
    public async Task<string> ErrorAsync()
    {
        try
        {
            return (await _error.WaitAsync(TimeSpan.FromSeconds(2))).Trim();
        }
        catch (TimeoutException)
        {
            return "";
        }
    }

    /// <summary>
    /// Sends SIGTERM. False when the helper had already exited, which the caller reports instead of crashing on.
    /// </summary>
    public bool Terminate()
    {
        if (kill(_process.Id, SigTerm) == 0) return true;

        var errno = Marshal.GetLastPInvokeError();
        if (errno == NoSuchProcess) return false;
        throw new InvalidOperationException($"Could not signal process {_process.Id}: errno {errno}");
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill.
        }
        _process.Dispose();
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int kill(int pid, int signal);
}
