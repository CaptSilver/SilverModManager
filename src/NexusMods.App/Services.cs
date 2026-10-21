using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Serialization;
using NexusMods.App.Commandline;
using NexusMods.App.UI;
using NexusMods.App.UI.Settings;
using NexusMods.Backend;
using NexusMods.CLI;
using NexusMods.Collections;
using NexusMods.CrossPlatform;
using NexusMods.DataModel;
using NexusMods.DataModel.JsonConverters;
using NexusMods.Games.AdvancedInstaller;
using NexusMods.Games.AdvancedInstaller.UI;
using NexusMods.Games.FileHashes;
using NexusMods.Games.FOMOD;
using NexusMods.Games.FOMOD.UI;
using NexusMods.Games.Generic;
using NexusMods.Library;
using NexusMods.Networking.EpicGameStore;
using NexusMods.Networking.GitHub;
using NexusMods.Networking.GOG;
using NexusMods.Networking.HttpDownloader;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Networking.Steam;
using NexusMods.Paths;
using NexusMods.Platform.Proton;
using NexusMods.Platform.Proton.Launch;
using NexusMods.ProxyConsole;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.ProxyConsole;
using NexusMods.Sdk.Settings;
using NexusMods.SingleProcess;

namespace NexusMods.App;

/// <summary>
/// Which service graph a process builds.
/// </summary>
public enum HostProfile
{
    /// <summary>The main process with the window and the CLI server.</summary>
    Gui,
    /// <summary>A main process without the window, the CLI server, the nxm handler or the installer views.</summary>
    Headless,
    /// <summary>A client that forwards its command to the main process.</summary>
    Slim,
    /// <summary>The Avalonia designer: the window's services, with no CLI server or sync file.</summary>
    Designer,
}

public static class Services
{
    public static IServiceCollection AddApp(
        this IServiceCollection services,
        bool addStandardGameLocators = true,
        StartupMode? startupMode = null,
        ExperimentalSettings? experimentalSettings = null)
    {
        services.Configure<HostOptions>(options =>
        {
            // Sequential execution can lead to long startup times depending on number of hostedServices.
            options.ServicesStartConcurrently = true;
            // If executed sequentially, one service taking a long time can trigger the timeout,
            // preventing StopAsync of other services from being called. 
            options.ServicesStopConcurrently = true;
        });
        startupMode ??= new StartupMode();
        var profile = ProfileOf(startupMode);
        if (profile == HostProfile.Slim)
        {
            services
                .AddSingleton<TimeProvider>(_ => TimeProvider.System)
                .AddFileSystem()
                .AddOSInterop()
                .AddRuntimeDependencies()
                .AddDefaultRenderers()
                .AddSettingsManager()
                .AddSingleton<JsonConverter, AbsolutePathConverter>()
                .AddSerializationAbstractions()
                .AddSettings<LoggingSettings>()
                .AddSingleProcess(Mode.Client);

            return services;
        }

        // The window's services go first: the main graph TryAdds headless fallbacks, such as the
        // non-interactive API key prompt, that must not land next to the window's own.
        if (profile is HostProfile.Gui or HostProfile.Designer)
            services.AddInteractiveServices();

        services.AddMainServices(registerNxmHandler: profile != HostProfile.Headless);

        switch (profile)
        {
            case HostProfile.Gui:
                // Only the window-showing process knows which executable Steam launch lines should come back to.
                services.AddSingleProcess(Mode.Main).AddLauncherShim();
                // Steam updates games while the app is closed, so managed games are checked once at startup.
                services.AddLocalBaselineStartupPass();
                break;
            case HostProfile.Designer:
                // The designer has no CLI server and no sync file: it must not touch the real instance's.
                break;
            case HostProfile.Headless:
                services.AddHeadlessServices(startupMode);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(profile), profile, null);
        }

        if (addStandardGameLocators)
            services.AddGameLocators();

        return services;
    }

    /// <summary>
    /// Picks the service graph for a process from how it was started.
    /// </summary>
    public static HostProfile ProfileOf(StartupMode startupMode)
    {
        if (startupMode.IsAvaloniaDesigner) return HostProfile.Designer;
        if (!startupMode.RunAsMain) return HostProfile.Slim;
        return startupMode.ShowUI ? HostProfile.Gui : HostProfile.Headless;
    }

    /// <summary>
    /// Everything a main process needs, with or without a window.
    /// </summary>
    private static IServiceCollection AddMainServices(this IServiceCollection services, bool registerNxmHandler)
    {
        return services
            .AddEpicGameStore()
            .AddSingleton<TimeProvider>(_ => TimeProvider.System)
            .AddDataModel()
            .AddLibrary()
            .AddLibraryModels()
            .AddJobMonitor()
            .AddNexusModsCollections()
            .AddProton()
            .AddHeadlessLaunch()

            .AddSettings<LoggingSettings>()
            .AddSettings<ExperimentalSettings>()
            .AddDefaultRenderers()
            .AddDefaultParsers()

            .AddSingleton<CommandLineConfigurator>()
            .AddCLI()
            .AddSettingsManager()
            .AddAdvancedInstaller()
            .AddFileExtractors()
            .AddSerializationAbstractions()
            .AddSupportedGames()
            .AddOSInterop()
            .AddRuntimeDependencies()
            .AddGames()
            .AddGameServices()
            .AddGenericGameSupport()
            .AddLoadoutAbstractions()
            .AddFomod()
            .AddNexusWebApi(registerNxmHandler)
            .AddHttpDownloader()
            // .AddAdvancedHttpDownloader()
            .AddFileSystem()
            .AddCleanupVerbs()
            .AddStatusVerbs()
            .AddSteamCli()
            .AddGOG()
            .AddFileHashes()
            .AddGitHubApi();
    }

    /// <summary>
    /// The window, the installer views and the app object that owns them.
    /// </summary>
    private static IServiceCollection AddInteractiveServices(this IServiceCollection services)
    {
        return services
            .AddUI()
            .AddSingleton<App>()
            .AddGuidedInstallerUi()
            .AddAdvancedInstallerUi();
    }

    /// <summary>
    /// A main process that runs verbs or a game launch without a window, a CLI server or an nxm handler.
    /// </summary>
    private static IServiceCollection AddHeadlessServices(this IServiceCollection services, StartupMode startupMode)
    {
        // The sync file is how a headless main announces itself to clients; only the server is left out.
        services
            .AddSingleton<SyncFile>()
            .AddSingleton<LaunchSessionSlot>()
            .AddSettings<CliSettings>()
            .AddTransient<CliClient>()
            .AddTransient<IGuidedInstaller, NullGuidedInstaller>()
            // A launch has to start quickly and work offline.
            .OverrideSettingsForTests<FileHashesServiceSettings>(settings => settings with { SkipUpdateCheck = true });

        // Signals must reach the launch's stop handler alone, not also stop the host under a running game.
        if (startupMode.IsHeadlessRun)
        {
            services
                .AddSingleton<IHostLifetime, NoSignalHostLifetime>()
                .AddSingleton(s => new StopHandler(s.GetRequiredService<ILoggerFactory>().CreateLogger<StopHandler>()));
        }

        return services;
    }
    
    private static IServiceCollection AddSupportedGames(this IServiceCollection services)
    {
        Games.RedEngine.Services.AddRedEngineGames(services);
        Games.StardewValley.Services.AddStardewValley(services);
        Games.Larian.BaldursGate3.Services.AddBaldursGate3(services);
        Games.CreationEngine.Services.AddCreationEngine(services);
        Games.MountAndBlade2Bannerlord.Services.AddMountAndBlade2Bannerlord(services);
        return services;
    }
}
