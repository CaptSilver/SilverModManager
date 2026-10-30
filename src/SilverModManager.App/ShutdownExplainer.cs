using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.App;

/// <summary>
/// Makes every way the process can stop say so in the log: a closed window, a POSIX signal, a stop of the host
/// from code, and the jobs that were still running when it began. A process that just vanishes mid-download
/// leaves only the last line of whatever service happened to log on the way down, which explains nothing.
/// </summary>
public sealed class ShutdownExplainer(IHostApplicationLifetime hostLifetime, IJobMonitor jobs, ILogger<ShutdownExplainer> logger) : IHostedService
{
    private readonly List<PosixSignalRegistration> _signals = [];
    private string? _reason;
    private bool _stopOnHangup;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        hostLifetime.ApplicationStopping.Register(OnStopping);

        // Registrations add to the host's console lifetime; they never replace it.
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGHUP })
            _signals.Add(PosixSignalRegistration.Create(signal, OnSignal));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in _signals) registration.Dispose();
        _signals.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records why the process is about to stop, for the stop that follows. The first reason wins, because a
    /// window close that triggers the host stop must not be re-explained as a stop from code.
    /// </summary>
    public void Note(string reason) => Interlocked.CompareExchange(ref _reason, reason, null);

    /// <summary>
    /// Watches the desktop lifetime. Having a window to close is also what makes SIGHUP stoppable: the default
    /// action kills the process before any log sink flushes, so a windowed app turns it into an ordinary stop.
    /// </summary>
    public void Attach(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _stopOnHangup = true;
        desktop.ShutdownRequested += (_, _) => logger.LogInformation("The desktop lifetime was asked to shut down");
        desktop.Exit += (_, e) => logger.LogInformation("The desktop lifetime exited with code {ExitCode}", e.ApplicationExitCode);
    }

    /// <summary>Logs why <paramref name="window"/> is closing.</summary>
    public void Watch(Window window) =>
        window.Closing += (_, e) =>
        {
            var description = DescribeWindowClose(e.CloseReason, e.IsProgrammatic);
            Note(description);
            logger.LogInformation("The {Window} is closing: {Description}", window.GetType().Name, description);
        };

    public static string DescribeWindowClose(WindowCloseReason reason, bool programmatic) => reason switch
    {
        _ when programmatic => "closed by the application",
        WindowCloseReason.OSShutdown => "the operating system is shutting down or logging out",
        WindowCloseReason.ApplicationShutdown => "application shutdown",
        WindowCloseReason.OwnerWindowClosing => "its owner window is closing",
        _ => "closed by the user",
    };

    public void OnSignal(PosixSignalContext context)
    {
        logger.LogInformation("Received {Signal}", context.Signal);
        Note($"received {context.Signal}");

        if (context.Signal != PosixSignal.SIGHUP || !_stopOnHangup) return;

        context.Cancel = true;
        hostLifetime.StopApplication();
    }

    private void OnStopping()
    {
        logger.LogInformation("Stopping: {Reason}", _reason ?? DescribeCaller(new StackTrace(skipFrames: 1)));

        var active = jobs.Jobs.Where(j => j.Status.IsActive()).ToArray();
        if (active.Length == 0) return;

        var described = string.Join(", ", active.Select(j => $"{j.Definition.GetType().Name} ({j.Status}{ProgressOf(j)})"));
        logger.LogInformation("{Count} active job(s) when the stop began, they end with the process: {Jobs}", active.Length, described);
    }

    private static string ProgressOf(IJob job) => job.Progress.HasValue ? $", {job.Progress.Value.Value:P0}" : "";

    /// <summary>
    /// The stop callbacks run on the thread that asked for the stop, so the stack still holds the asker. Frames of
    /// the framework are skipped; the console lifetime is the one worth naming, since that is a signal.
    /// </summary>
    private static string DescribeCaller(StackTrace stack)
    {
        foreach (var frame in stack.GetFrames())
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            if (method is null || type is null) continue;
            if (type.FullName?.Contains("ConsoleLifetime", StringComparison.Ordinal) == true)
                return "the host's console lifetime (SIGINT, SIGQUIT or SIGTERM)";
            if (type.Namespace is { } ns && (ns.StartsWith("System", StringComparison.Ordinal) || ns.StartsWith("Microsoft", StringComparison.Ordinal))) continue;
            if (type == typeof(ShutdownExplainer)) continue;

            return $"the host was stopped from code, by {type.Name}.{method.Name}";
        }

        return "the host was stopped from code, caller unknown";
    }
}
