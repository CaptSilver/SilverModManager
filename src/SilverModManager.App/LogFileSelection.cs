using SilverModManager.CrossPlatform;
using SilverModManager.Sdk;

namespace SilverModManager.App;

/// <summary>
/// Picks which of the log files in <see cref="LoggingSettings"/> a process writes to.
/// </summary>
public static class LogFileSelection
{
    /// <summary>
    /// A <c>run</c> process gets its own file so a game launch is not interleaved with the
    /// log of the GUI or of a headless main process that happens to be running.
    /// </summary>
    public static (ConfigurablePath Current, ConfigurablePath Archive) Select(LoggingSettings settings, StartupMode startupMode)
    {
        if (startupMode.IsHeadlessRun) return (settings.RunProcessLogFilePath, settings.RunProcessArchiveFilePath);
        if (startupMode.RunAsMain) return (settings.MainProcessLogFilePath, settings.MainProcessArchiveFilePath);
        return (settings.SlimProcessLogFilePath, settings.SlimProcessArchiveFilePath);
    }
}
