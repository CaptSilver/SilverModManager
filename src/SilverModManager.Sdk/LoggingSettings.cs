using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.CrossPlatform;

/// <summary>
/// Settings related to logging in SilverModManager. 
/// </summary>
public record LoggingSettings : ISettings
{
    /// <summary>
    /// Gets the path to the current log file for the main process.
    /// </summary>
    public ConfigurablePath MainProcessLogFilePath { get; init; }

    /// <summary>
    /// Gets the template path to the archive log file for the main process.
    /// </summary>
    /// <remarks>
    /// For details, see https://nlog-project.org/documentation/v5.0.0/html/P_NLog_Targets_FileTarget_ArchiveFileName.htm
    /// </remarks>
    public ConfigurablePath MainProcessArchiveFilePath { get; init; }

    /// <summary>
    /// Gets the path to the current log file for the slim process.
    /// </summary>
    public ConfigurablePath SlimProcessLogFilePath { get; init; }

    /// <summary>
    /// Gets the path to the current log file for the slim process.
    /// </summary>
    public ConfigurablePath SlimProcessArchiveFilePath { get; init; }

    /// <summary>
    /// Gets the path to the current log file for a headless <c>run</c> process.
    /// </summary>
    public ConfigurablePath RunProcessLogFilePath { get; init; }

    /// <summary>
    /// Gets the template path to the archive log file for a headless <c>run</c> process.
    /// </summary>
    public ConfigurablePath RunProcessArchiveFilePath { get; init; }

    /// <summary>
    /// Number of previous log files to store.
    /// </summary>
    public int MaxArchivedFiles { get; init; } = 10;

    /// <summary>
    /// Gets the minimum logging level.
    /// </summary>
    public LogLevel MinimumLevel { get; [UsedImplicitly] set; } = LogLevel.Debug;

    /// <summary>
    /// When enabled, logs will be written to the console as well as the log file.
    /// </summary>
    public bool LogToConsole { get; [UsedImplicitly] set; } = ApplicationConstants.IsDebug;

    /// <summary>
    /// Gets the retention span for process logs.
    /// </summary>
    public TimeSpan ProcessLogRetentionSpan { get; } = TimeSpan.FromDays(7);

    /// <summary>
    /// When enabled, shows an exception modal to the user on every observed exception.
    /// </summary>
    public bool ShowExceptions { get; [UsedImplicitly] set; } = true;

    /// <inheritdoc/>
    public static string SettingsId => "SilverModManager.CrossPlatform.LoggingSettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureDefault(_ => CreateDefault())
            .ConfigureBackend(StorageBackendOptions.Use(StorageBackends.Json))
            .ConfigureProperty(
                x => x.MinimumLevel,
                new PropertyOptions<LoggingSettings, LogLevel>
                {
                    Section = Sections.General,
                    DisplayName = "Minimum logging level",
                    DescriptionFactory = _ => "Sets the minimum logging level. Recommended: Debug",
                    RequiresRestart = true,
                },
                SingleValueMultipleChoiceContainerOptions.Create(
                    valueComparer: EqualityComparer<LogLevel>.Default,
                    allowedValues:
                    [
                        LogLevel.Information,
                        LogLevel.Debug,
                        LogLevel.Trace,
                    ],
                    valueToDisplayString: static logLevel => logLevel.ToString()
                )
            )
            .ConfigureProperty(
                x => x.LogToConsole,
                new PropertyOptions<LoggingSettings, bool>
                {
                    Section = Sections.DeveloperTools,
                    DisplayName = "Log to console",
                    DescriptionFactory = _ => "Enables the ConsoleTarget (stdout) for all loggers.",
                    RequiresRestart = true,
                },
                new BooleanContainerOptions()
            )
            .ConfigureProperty(
                x => x.ShowExceptions,
                new PropertyOptions<LoggingSettings, bool>
                {
                    Section = Sections.DeveloperTools,
                    DisplayName = "Show exception modal",
                    DescriptionFactory = _ => "Enables the exception modal.",
                    RequiresRestart = true,
                },
                new BooleanContainerOptions()
            );
    }

    public static AbsolutePath GetLogBaseFolder(IFileSystem fs)
    {
        return fs.GetKnownPath(BaseKnownPath).Combine(BaseDirectoryName);
    }

    public static LoggingSettings CreateDefault()
    {
        return new LoggingSettings
        {
            MainProcessLogFilePath = new ConfigurablePath(BaseKnownPath, $"{BaseDirectoryName}/{LogFilePrefix}.main.current.log"),
            MainProcessArchiveFilePath = new ConfigurablePath(BaseKnownPath, $"{BaseDirectoryName}/{LogFilePrefix}.main.{{##}}.log"),
            SlimProcessLogFilePath = new ConfigurablePath(BaseKnownPath, $"{BaseDirectoryName}/{LogFilePrefix}.slim.current.log"),
            SlimProcessArchiveFilePath = new ConfigurablePath(BaseKnownPath, $"{BaseDirectoryName}/{LogFilePrefix}.slim.{{##}}.log"),
            RunProcessLogFilePath = new ConfigurablePath(BaseKnownPath, $"{BaseDirectoryName}/{LogFilePrefix}.run.current.log"),
            RunProcessArchiveFilePath = new ConfigurablePath(BaseKnownPath, $"{BaseDirectoryName}/{LogFilePrefix}.run.{{##}}.log"),
        };
    }

    private const KnownPath BaseKnownPath = KnownPath.XDG_STATE_HOME;
    private const string BaseDirectoryName = $"{ApplicationConstants.DataDirectoryName}/Logs";

    // Lowercase, as log file names conventionally are; the product name has capitals.
    private const string LogFilePrefix = "silvermodmanager";
}
