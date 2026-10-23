using System.Diagnostics;
using NexusMods.Paths;

namespace SilverModManager.Games.FromSoft.Overlay;

/// <summary>Asks whether bwrap can make a user namespace on this machine.</summary>
public interface ISandboxProbe
{
    /// <summary>
    /// Runs <c>bwrap --unshare-user --bind / / true</c>. Returns null when it worked, otherwise why it did not.
    /// </summary>
    string? Probe(AbsolutePath bwrap);
}

/// <summary>
/// Runs the probe in an empty sandbox: the command is <c>true</c> and nothing of any game is mounted.
/// </summary>
internal sealed class BwrapSandboxProbe : ISandboxProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public string? Probe(AbsolutePath bwrap)
    {
        var start = new ProcessStartInfo(bwrap.GetFullPath())
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        foreach (var arg in (string[])[..OverlayCommand.UserNamespace, ..OverlayCommand.RootBind, "true"]) start.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("the process did not start");
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return $"it did not finish within {Timeout.TotalSeconds:0} seconds";
            }

            return process.ExitCode == 0 ? null : $"it exited with code {process.ExitCode}: {error.GetAwaiter().GetResult().Trim()}";
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return e.Message;
        }
    }
}
