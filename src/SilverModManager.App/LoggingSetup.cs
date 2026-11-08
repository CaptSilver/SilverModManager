using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;
using NLog.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using Target = NLog.Targets.Target;

namespace SilverModManager.App;

/// <summary>
/// Builds the NLog configuration the app logs through. Every sink is written by its own background thread, so no
/// caller (the UI thread included) waits for a disk, a terminal or a subscriber.
/// </summary>
public static class LoggingSetup
{
    /// <summary>
    /// Routes every level to every sink in <paramref name="sinks"/>, each behind its own queue.
    /// </summary>
    public static LoggingConfiguration CreateConfiguration(IEnumerable<Target> sinks)
    {
        var config = new LoggingConfiguration();
        foreach (var sink in sinks) config.AddRuleForAllLevels(Queued(sink));
        return config;
    }

    /// <summary>
    /// Puts <paramref name="sink"/> behind a queue that grows instead of blocking or dropping. A caller waiting on a
    /// saturated disk froze the window, and a dropped line is a hole in the very log used to diagnose a hang or crash.
    /// The cost is memory while the writer is behind: a few hundred bytes a line, and the writer drains it as soon as
    /// the disk answers. <c>QueueLimit</c> is only the size at which NLog starts doubling the queue.
    /// </summary>
    private static Target Queued(Target sink)
    {
        return new AsyncTargetWrapper(sink)
        {
            OverflowAction = AsyncTargetWrapperOverflowAction.Grow,
            QueueLimit = 10_000,
            BatchSize = 1_000,
        };
    }

    /// <summary>
    /// Whether the environment asks for <see cref="SynchronizerLogging.PerFileCategory"/>.
    /// </summary>
    public static bool PerFileRequested(IEnvironmentReader environment) =>
        environment.Get(SynchronizerLogging.PerFileEnvironmentVariable) == "1";

    /// <summary>
    /// Sends <paramref name="builder"/>'s logs to <paramref name="factory"/> at <paramref name="minimum"/> and above.
    /// Hides <see cref="SynchronizerLogging.PerFileCategory"/> unless <paramref name="perFileRequested"/>: the minimum
    /// level is global, so without this a user at Trace gets a line per file per pass. One session wrote 9 million of
    /// them (2 GB) and the file writes slowed the whole app.
    /// </summary>
    public static void ConfigureBuilder(ILoggingBuilder builder, LogFactory factory, LogLevel minimum, bool perFileRequested)
    {
        builder.AddFilter("Microsoft", LogLevel.Warning);
        builder.AddFilter("System", LogLevel.Warning);
        builder.AddFilter("Avalonia.ReactiveUI", LogLevel.Warning);
        if (!perFileRequested) builder.AddFilter(SynchronizerLogging.PerFileCategory, LogLevel.Debug);

        builder.ClearProviders();
        builder.SetMinimumLevel(minimum);

        // NOTE(erri120): RemoveLoggerFactoryFilter prevents
        // the global minimum level to take effect.
        // https://github.com/Nexus-Mods/NexusMods.App/issues/250
        builder.AddNLog(new NLogProviderOptions { RemoveLoggerFactoryFilter = false }, _ => factory);
    }

    /// <summary>
    /// Writes out everything still queued, then stops the writers. Called when the process is going away so the last
    /// lines before an exit or a crash reach the file.
    /// </summary>
    public static void Shutdown(LogFactory factory) => factory.Shutdown();

    /// <summary>
    /// Flushes <paramref name="factory"/> when the process exits or dies from an unhandled exception. The runtime
    /// raises no exit event for a crash, so the crash itself is logged first and then flushed.
    /// </summary>
    public static void FlushOnProcessEnd(LogFactory factory)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            factory.GetLogger("Unhandled").Fatal(args.ExceptionObject as Exception, "Unhandled exception, the process is going down");
            factory.Flush(TimeSpan.FromSeconds(5));
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown(factory);
    }
}
