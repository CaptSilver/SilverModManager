using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Logging;
using NexusMods.Abstractions.Serialization;
using NexusMods.Sdk.Settings;
using NexusMods.App.Commandline;
using NexusMods.App.UI;
using NexusMods.App.UI.Settings;
using NexusMods.Backend;
using NexusMods.Games.FileHashes;
using NexusMods.CrossPlatform;
using NexusMods.DataModel;
using NexusMods.DataModel.SchemaVersions;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.MnemonicDB.Storage.Abstractions;
using NexusMods.Paths;
using NexusMods.Platform.Proton;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Platform.Proton.Verbs;
using NexusMods.ProxyConsole;
using NexusMods.Sdk;
using NexusMods.Secrets;
using NexusMods.SingleProcess;
using NexusMods.SingleProcess.Exceptions;
using NLog.Extensions.Logging;
using NLog.Targets;
using ReactiveUI;
using Spectre.Console;

namespace NexusMods.App;

public class Program
{
    private static ILogger<Program> _logger = default!;

    [STAThread]
    public static int Main(string[] args)
    {
        MainThreadData.SetMainThread();

        // Reads lock files only: no settings, logging or host, so it answers while a game or the app is running.
        var startupMode = StartupMode.Parse(args);
        if (startupMode.IsStatus)
        {
            return StatusCommand.Run(startupMode.Args, Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), "/proc/self/mountinfo", Console.Out, Console.Error);
        }

        // Usage text only: no settings, logging or host, so `run --help` can neither fail nor start a launch.
        if (startupMode.IsRunHelp)
        {
            Console.Out.WriteLine(LaunchArgs.HelpText);
            return 0;
        }

        // Checks flock and signals on this binary only: no settings, logging or host, so nothing unrelated can fail or stall it.
        if (startupMode.IsSelftestHelper)
        {
            return SelftestHelper.Run(startupMode.Args, Console.Out, Console.Error);
        }

        LoggingSettings loggingSettings;
        using (var settingsHost = BuildSettingsHost())
        {
            var settingsManager = settingsHost.Services.GetRequiredService<ISettingsManager>();
            loggingSettings = settingsManager.Get<LoggingSettings>();
        }

        var steps = CreateSteps(loggingSettings);
        return ProcessStartup.Run(startupMode, args, steps);
    }

    private static ProcessStartupSteps CreateSteps(LoggingSettings loggingSettings) => new()
    {
        BuildHost = startupMode =>
        {
            var host = BuildHost(startupMode, loggingSettings);
            _logger = host.Services.GetRequiredService<ILogger<Program>>();
            return host;
        },
        ReadSyncInfo = services => services.GetRequiredService<SyncFile>().GetSyncInfo(),
        CanConnect = (services, port) => CanConnectToProcess(
            services.GetRequiredService<ILogger<Program>>(), port, timeout: TimeSpan.FromSeconds(6), services: services
        ),
        Kill = process => process.Kill(entireProcessTree: true),
        OpenDatabase = OpenDatabase,
        TryClaimMain = (services, kind, port) => services.GetRequiredService<SyncFile>().TrySetMain(kind, port),
        ReleaseMain = services => services.GetRequiredService<SyncFile>().ReleaseMain(),
        CloseDatabase = CloseDatabase,
        ReleaseLaunchSession = services => services.GetService<LaunchSessionSlot>()?.Current?.Release(),
        Notify = (services, message) => services.GetRequiredService<IDesktopNotifier>().Notify("SilverModManager", message),
        InstallGlobalHandlers = services =>
        {
            var logger = services.GetRequiredService<ILogger<Program>>();
            TaskScheduler.UnobservedTaskException += (sender, eventArgs) =>
            {
                LogMessages.UnobservedTaskException(logger, eventArgs.Exception, sender, sender?.GetType());
                eventArgs.SetObserved();
            };

            RxApp.DefaultExceptionHandler = Observer.Create<Exception>(ex =>
            {
                LogMessages.UnobservedReactiveThrownException(logger, ex);
            });
        },
        RunCliAsMain = (services, startupMode) => RunCliTaskAsMain(services, startupMode),
        RunCliRemotely = (services, startupMode) => RunCliTaskRemotely(services, startupMode),
        RunLaunch = LaunchRun.RunAsync,
        TryPassThrough = LaunchRun.TryPassThroughAsync,
        DescribeHeadlessMain = (services, kind, pid) => HeadlessMainRefusal.Describe(kind, pid, services.GetRequiredService<IEnvironmentReader>().Get("XDG_RUNTIME_DIR")),
        RunUi = services => Startup.Main(services, []),
    };

    /// <summary>
    /// Closes the connection, the store and the RocksDB backend under it, in that order.
    /// </summary>
    /// <remarks>
    /// Only the backend's dispose closes RocksDB and lets go of its lock file. The container would get to it
    /// when the host is disposed, but that is after the sync file slot is released, and the next main opens
    /// the database as soon as it holds the slot. All three disposes are idempotent, so the container's later
    /// pass is harmless.
    /// </remarks>
    public static void CloseDatabase(IServiceProvider services)
    {
        (services.GetService<IConnection>() as IDisposable)?.Dispose();
        (services.GetService<IDatomStore>() as IDisposable)?.Dispose();
        (services.GetService<IStoreBackend>() as IDisposable)?.Dispose();
    }

    private static void OpenDatabase(IServiceProvider services)
    {
        var dataModelSettings = services.GetRequiredService<ISettingsManager>().Get<DataModelSettings>();
        var fileSystem = services.GetRequiredService<IFileSystem>();
        var osInterop = services.GetRequiredService<IOSInterop>();

        var modelExists = dataModelSettings.MnemonicDBPath.ToPath(fileSystem).DirectoryExists();

        // Both apps would treat a game folder managed by the other as their own, so a
        // loadout left applied by upstream gets recorded as the game's original state.
        if (UpstreamData.Exists(fileSystem))
        {
            _logger.LogWarning("Upstream Nexus Mods App data found at {Path}; unmanage shared games there first", UpstreamData.DataModelPath(fileSystem));
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var archiveLocation = dataModelSettings.ArchiveLocations[0].ToPath(fileSystem);
                var mount = await osInterop.GetFileSystemMount(archiveLocation);
                if (mount is not null) _logger.LogInformation("Archives are stored at {Path} on mount {Mount}", archiveLocation, mount);
                else _logger.LogWarning("Failed to find file system mount for {Path}", archiveLocation);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to get file system mounts");
            }
        });

        var migration = services.GetRequiredService<MigrationService>();
        if (modelExists)
        {
            // Run the migrations
            migration.MigrateAll().Wait();
        }
        else
        {
            // Otherwise, perform the initial setup
            migration.InitialSetup().Wait();
        }

        // Bounded and never prompting: a keyring that is down leaves the app signed out, it does not stop it starting.
        SecretStartup.PrimeAsync(services, CancellationToken.None).Wait();
    }

    private static async Task<bool> CanConnectToProcess(ILogger logger, int port, TimeSpan timeout, IServiceProvider services)
    {
        var client = services.GetRequiredService<CliClient>();
        var cts = new CancellationTokenSource(delay: timeout);

        try
        {
            // Run the heartbeat command to check if the process is responsive
            var commandTask = client.ExecuteCommand([StatusVerbs.HeartbeatCommand], AnsiConsole.Console);
            
            // Wait for the command to complete or the timeout to expire
            await commandTask.WaitAsync(cts.Token);
            return true;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Exception connecting to process on `{Port}`", port);
            return false;
        }
    }

    private static async Task<int> RunCliTaskRemotely(IServiceProvider services, StartupMode startupMode)
    {
        var client = services.GetRequiredService<CliClient>();
        var syncFile = services.GetRequiredService<SyncFile>();
        try
        {
            await client.ExecuteCommand(startupMode.Args, AnsiConsole.Console);
            return 0;
        }
        catch (NoMainProcessStarted _) when (!startupMode.StartsUiWhenNoMainRunning)
        {
            // The verb needs the main process because that is what owns the database. Run
            // it in a headless main that exits when the verb is done. Starting a plain main
            // (no arguments) would open the UI and leave the process running.
            var interop = services.GetRequiredService<IOSInterop>();
            var ownExe = interop.GetRunningExecutablePath(out var _);
            _logger.LogInformation("No main process started, running verb in headless main {OwnExe}", ownExe);
            var processInfo = new ProcessStartInfo
            {
                FileName = ownExe.ToString(),
                // Inherit stdio so the verb's output reaches this terminal.
                UseShellExecute = false,
            };
            foreach (var arg in startupMode.HeadlessMainArgs) processInfo.ArgumentList.Add(arg);

            using var process = Process.Start(processInfo);
            if (process is null)
            {
                _logger.LogError("Failed to start headless main {OwnExe}", ownExe);
                return 1;
            }

            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (NoMainProcessStarted _)
        {
            var interop = services.GetRequiredService<IOSInterop>();
            var ownExe = interop.GetRunningExecutablePath(out var _);
            _logger.LogInformation("No main process started, starting {OwnExe}", ownExe);
            var processInfo = new ProcessStartInfo
            {
                FileName = ownExe.ToString(),
                Arguments = "",
                UseShellExecute = true,
            };
            var process = Process.Start(processInfo);
            if (process is null)
            {
                _logger.LogError("Failed to start main process {OwnExe}", ownExe);
                return 1;
            }
            else
            {
                _logger.LogInformation("Started main process {OwnExe} with id {ProcessId}", ownExe, process.Id);
            }

            var st = Stopwatch.StartNew();
            while (!syncFile.IsMainRunning)
            {
                await Task.Delay(100);
                if (st.Elapsed > TimeSpan.FromSeconds(60))
                {
                    _logger.LogError("Main process {OwnExe} did not start", ownExe);
                    return 1;
                }
            }

            await client.ExecuteCommand(startupMode.Args, AnsiConsole.Console);
            return 0;
        }
    }

    /// <summary>
    /// Runs the CLI task in this process. 
    /// </summary>
    private static Task<int> RunCliTaskAsMain(IServiceProvider provider, StartupMode startupMode)
    {
        if (!startupMode.ExecuteCli)
            return Task.FromResult(0);
        var configurator = provider.GetRequiredService<CommandLineConfigurator>();
        _logger.LogInformation("Starting with Spectre.Cli");
        return configurator.RunAsync(startupMode.Args, new SpectreRenderer(AnsiConsole.Console), CancellationToken.None);
    }

    private static IHost BuildSettingsHost()
    {
        var host = new HostBuilder()
            .ConfigureServices(services => services
                .AddSingleton(FileSystem.Shared)
                .AddSettingsManager()
                .AddSerializationAbstractions()
                .AddStorageBackend<JsonStorageBackend>()
                .AddSettings<LoggingSettings>()
                .AddSettings<ExperimentalSettings>()
                .AddSettings<CliSettings>()
            )
            .ConfigureLogging((_, builder) => builder
                .ClearProviders()
                .AddSimpleConsole()
                .SetMinimumLevel(LogLevel.Trace)
            )
            .Build();

        return host;
    }

    /// <summary>
    /// Constructs the host for the application, if <paramref name="slimMode"/> is true, the host will not register
    /// most of the services, and will only register what is required to proxy the app to the main process.
    /// <paramref name="isAvaloniaDesigner"/> should be set to true when constructing the host for the Avalonia Designer
    /// and will use the in memory database 
    /// </summary>
    private static IHost BuildHost(
        StartupMode startupMode,
        LoggingSettings loggingSettings)
    {
        var observableTarget = new ObservableLoggingTarget();
        var host = new HostBuilder().ConfigureServices(services =>
        {
            var s = services.AddApp(startupMode: startupMode).Validate();

            if (loggingSettings.ShowExceptions)
                s.AddSingleton<IObservableExceptionSource, ObservableLoggingTarget>(_ => observableTarget);

            if (startupMode.IsAvaloniaDesigner)
            {
                s.OverrideSettingsForTests<DataModelSettings>(settings => settings with { UseInMemoryDataModel = true, });
                s.OverrideSettingsForTests<FileHashesServiceSettings>(settings => settings with { UseInMemoryLocalBaseline = true, });
            }
        })
        .ConfigureLogging((_, builder) => AddLogging(observableTarget, builder, loggingSettings, startupMode))
        .Build();

        return ApplicationConstants.IsDebug ? new DebuggingHost(host) : host;
    }

    private static void AddLogging(ObservableLoggingTarget observableLoggingTarget, ILoggingBuilder loggingBuilder, LoggingSettings settings, StartupMode startupMode)
    {
        var fs = FileSystem.Shared;
        var config = new NLog.Config.LoggingConfiguration();

        const string defaultLayout = "${processtime} [${level:uppercase=true}] (${logger}) ${message:withexception=true}";
        const string defaultHeader = "############ SilverModManager log file - ${longdate} ############";

        var (logFile, archiveFile) = LogFileSelection.Select(settings, startupMode);
        var fileTarget = new FileTarget("file")
        {
            FileName = logFile.ToPath(fs).GetFullPath(),
            ArchiveFileName = archiveFile.ToPath(fs).GetFullPath(),
        };

        fileTarget.ArchiveOldFileOnStartup = true;
        fileTarget.MaxArchiveDays = settings.MaxArchivedFiles;
        fileTarget.Layout = defaultLayout;
        fileTarget.Header = defaultHeader;

        if (settings.LogToConsole)
        {
            var consoleTarget = new ConsoleTarget("console")
            {
                Layout = "${processtime} [${level:uppercase=true}] ${message:withexception=true}",
            };
            config.AddRuleForAllLevels(consoleTarget);
        }

        config.AddRuleForAllLevels(fileTarget);
        config.AddRuleForAllLevels(observableLoggingTarget);


        // NOTE(erri120): RemoveLoggerFactoryFilter prevents
        // the global minimum level to take effect.
        // https://github.com/Nexus-Mods/NexusMods.App/issues/250
        var options = new NLogProviderOptions
        {
            RemoveLoggerFactoryFilter = false,
        };

        loggingBuilder.AddFilter("Microsoft", LogLevel.Warning);
        loggingBuilder.AddFilter("System", LogLevel.Warning);
        loggingBuilder.AddFilter("Avalonia.ReactiveUI", LogLevel.Warning);

        loggingBuilder.ClearProviders();
        loggingBuilder.SetMinimumLevel(settings.MinimumLevel);
        loggingBuilder.AddNLog(config, options);
    }

    /// <summary>
    /// Don't Delete this method. It's used by the Avalonia Designer.
    /// </summary>
    // ReSharper disable once UnusedMember.Local
    // ReSharper disable once UnusedMember.Global
    public static AppBuilder BuildAvaloniaApp()
    {
        var startupMode = new StartupMode()
        {
            RunAsMain = true,
            ShowUI = false,
            ExecuteCli = false,
            IsAvaloniaDesigner = true,
            Args = [],
            OriginalArgs = [],
        };

        var host = BuildHost(startupMode, LoggingSettings.CreateDefault());

        host.StartAsync().GetAwaiter().GetResult();

        DesignerUtils.Activate(host.Services);

        return Startup.BuildAvaloniaApp(host.Services);
    }
}

file class DebuggingHost : IHost
{
    private readonly IHost _inner;
    private readonly ILogger _logger;

    public DebuggingHost(IHost inner)
    {
        _inner = inner;
        _logger = inner.Services.GetRequiredService<ILogger<DebuggingHost>>();
    }

    public Task StartAsync(CancellationToken cancellationToken) => _inner.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => _inner.StopAsync(cancellationToken);
    public IServiceProvider Services => _inner.Services;

    [SuppressMessage("ReSharper", "LocalizableElement")]
    public void Dispose()
    {
        // NOTE(erri120): I'm doing reflection and you can't stop me.
        if (_inner.Services is not ServiceProvider services) throw new NotSupportedException();

        var rootPropertyInfo = services.GetType().GetProperty(name: "Root", BindingFlags.Instance | BindingFlags.NonPublic);
        if (rootPropertyInfo is null) throw new NotSupportedException();

        var root = rootPropertyInfo.GetMethod?.Invoke(services, parameters: null);
        if (root is not IServiceScope scope) throw new NotSupportedException();
        if (scope.GetType().Name != "ServiceProviderEngineScope") throw new NotSupportedException();

        var fieldInfo = scope.GetType().GetField("_disposables", BindingFlags.NonPublic | BindingFlags.Instance);
        if (fieldInfo is null) throw new NotSupportedException();

        var fieldValue = fieldInfo.GetValue(scope);
        if (fieldValue is not List<object> tempList) throw new NotSupportedException();

        var disposableServices = tempList.ToArray();
        Log("Disposing `{0}` services", disposableServices.Length);

        Reloaded.Memory.Utilities.Box<bool> didDispose = false;

        _ = Task.Run(async () =>
        {
            var delay = TimeSpan.FromSeconds(5);
            await Task.Delay(delay);

            // ReSharper disable once AccessToModifiedClosure
            bool isDisposed = didDispose;
            if (isDisposed) return;

            Log("Failed to dispose `{0}` services withing `{1}` seconds", disposableServices.Length, delay.TotalSeconds);
            foreach (var disposableService in disposableServices)
            {
                var disposableType = disposableService switch
                {
                    IDisposable => "sync",
                    IAsyncDisposable => "async",
                    _ => throw new NotSupportedException(),
                };

                Log("Type={0} HashCode={1} DisposableType={2}", disposableService.GetType(), disposableService.GetHashCode(), disposableType);
            }

            // NOTE(erri120): If you landed here, that means the app is probably stuck shutting down.
            // Use this opportunity to further debug the issue. You can see all the services that need
            // disposing by inspecting the variables above and checking the logs.
            if (Debugger.IsAttached) Debugger.Break();
            // if (ApplicationConstants.IsCI) Environment.Exit(exitCode: 1);
        });

        _inner.Dispose();
        didDispose = true;
    }

    private void Log(string format, params object?[] arguments)
    {
        _logger.LogDebug(format, arguments);
        if (ApplicationConstants.IsCI) Console.WriteLine(format, arguments);
    }
}
