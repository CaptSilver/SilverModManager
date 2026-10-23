using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// The hidden <c>selftest-helper</c> subcommands. The selftest spawns the shipped binary with these to check
/// flock and signal behaviour on the real executable. They run before any host, database or sync file exists:
/// a host would install ConsoleLifetime, which handles SIGTERM before <see cref="StopHandler"/> can.
/// </summary>
public static class SelftestHelper
{
    public const string Usage = "usage: selftest-helper ping | wait-for-term | hold-lock <path> <seconds>";

    private const int LockHeldElsewhere = 3;
    private const int TimedOut = 4;
    private static readonly TimeSpan WaitForTermTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs one subcommand and returns the process exit code. Each one prints a line the selftest reads
    /// to know the helper is ready, because it must not signal or probe the process before that.
    /// </summary>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        switch (args)
        {
            case ["ping"]:
                output.WriteLine("pong");
                return 0;
            case ["wait-for-term"]:
                return WaitForTerm(output);
            case ["hold-lock", var path, var seconds] when double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var hold) && hold >= 0:
                return HoldLock(path, TimeSpan.FromSeconds(hold), output);
            default:
                error.WriteLine(Usage);
                return 2;
        }
    }

    private static int WaitForTerm(TextWriter output)
    {
        using var handler = new StopHandler(NullLogger.Instance);
        var stopped = new ManualResetEventSlim();
        handler.Stopping += (_, _) => stopped.Set();
        output.WriteLine("ready");
        output.Flush();

        if (!stopped.Wait(WaitForTermTimeout)) return TimedOut;

        // The exit code a shell reports for death by that signal, which is what an unhandled one would have given.
        return 128 + handler.Signal switch
        {
            PosixSignal.SIGHUP => 1,
            PosixSignal.SIGINT => 2,
            _ => 15,
        };
    }

    private static int HoldLock(string path, TimeSpan hold, TextWriter output)
    {
        using var installationLock = new InstallationLock(path);
        if (!installationLock.TryAcquire()) return LockHeldElsewhere;

        output.WriteLine("locked");
        output.Flush();
        Thread.Sleep(hold);
        return 0;
    }
}
