using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.GuidedInstallers;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Abstractions.Serialization;
using SilverModManager.App.Commandline;
using SilverModManager.App.UI;
using SilverModManager.App.UI.Settings;
using SilverModManager.Backend;
using SilverModManager.CLI;
using SilverModManager.Collections;
using SilverModManager.CrossPlatform;
using SilverModManager.DataModel;
using SilverModManager.DataModel.JsonConverters;
using SilverModManager.Games.AdvancedInstaller;
using SilverModManager.Games.AdvancedInstaller.UI;
using SilverModManager.Games.FileHashes;
using SilverModManager.Games.FOMOD;
using SilverModManager.Games.FOMOD.UI;
using SilverModManager.Games.Generic;
using SilverModManager.Library;
using SilverModManager.Networking.EpicGameStore;
using SilverModManager.Networking.GitHub;
using SilverModManager.Networking.GOG;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Networking.NexusWebApi;
using SilverModManager.Networking.Steam;
using SilverModManager.Networking.Thunderstore;
using NexusMods.Paths;
using SilverModManager.Platform.Proton;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.ProxyConsole;
using SilverModManager.Sdk.Library;
using SilverModManager.Secrets;
using SilverModManager.Secrets.SecretService;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Settings;
using SilverModManager.SingleProcess;
using SilverModManager.Wabbajack;

namespace SilverModManager.App;

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
        StartupMode? startupMode = null)
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

        services.AddMainServices(registerNxmHandler: profile != HostProfile.Headless, profile, startupMode);

        switch (profile)
        {
            case HostProfile.Gui:
                // Only the window-showing process knows which executable Steam launch lines should come back to.
                services.AddSingleProcess(Mode.Main).AddLauncherShim();
                // A Steam launch option's `run` hands its game to the window, which owns the database.
                services.AddSingleton<IForwardedLaunchHandler, ForwardedLaunchHandler>();
                // A game started here must leave the window's own shutdown signals alone.
                services.LeaveSignalsToHost();
                // The host's console lifetime swallows SIGTERM/SIGINT; the window has to be told to close.
                services.TryAddSingleton<DesktopShutdownOnHostStop>();
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
    private static IServiceCollection AddMainServices(this IServiceCollection services, bool registerNxmHandler, HostProfile profile, StartupMode startupMode)
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
            .AddYamlUserGameInstallStore()
            .AddGenericGameSupport()
            .AddLoadoutAbstractions()
            .AddFomod()
            // Before the consumers of credentials: the window can show an unlock prompt, every other process never does.
            .AddSecrets(new SecretStoreOptions
            {
                Mode = profile == HostProfile.Gui ? SecretStoreMode.Interactive : SecretStoreMode.NonInteractive,
                SecretsFile = startupMode.SecretsFile,
                Backend = startupMode.SecretBackend,
                // A launch hands the machine to a game right after start-up and must not spend it on tidying the database.
                CompactAfterMove = !startupMode.IsHeadlessRun,
            })
            .AddSecretServiceBackend()
            .AddNexusWebApi(registerNxmHandler)
            .AddHttpDownloader()
            // .AddAdvancedHttpDownloader()
            .AddFileSystem()
            .AddCleanupVerbs()
            .AddStatusVerbs()
            .AddSteamCli()
            .AddGOG()
            .AddFileHashes()
            .AddGitHubApi()
            .AddThunderstore()
            .AddWabbajack();
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
        Games.FromSoft.Services.AddFromSoftGames(services);
        Games.Unity.Services.AddUnityGames(services);
        return services;
    }
}
