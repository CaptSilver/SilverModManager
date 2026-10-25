using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Secrets;
using SilverModManager.Abstractions.Steam;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Cleanup;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Games.FromSoft.Doctor;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Heal;
using SilverModManager.Games.FromSoft.Launch;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Games.FromSoft.Overlay;
using SilverModManager.Games.FromSoft.Saves;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.FromSoft;

public static class Services
{
    /// <summary>Registers the FromSoft base and every FromSoft game.</summary>
    public static IServiceCollection AddFromSoftGames(this IServiceCollection services)
        => services.AddFromSoftBase().AddEldenRing();

    /// <summary>Registers Elden Ring: the game, its tool and its cleanup verb.</summary>
    public static IServiceCollection AddEldenRing(this IServiceCollection services)
    {
        services.AddGame<EldenRingGame>()
            .AddSingleton<ITool, RunEldenRingGame>();

        return services.AddVerb(() => EldenRingCleanupVerb.Cleanup)
            .AddModule("elden-ring", "Commands for the Elden Ring installation");
    }

    /// <summary>
    /// Registers everything the FromSoft games share. Each block takes the lines of one kind of registration, so two
    /// changes that add to different blocks never touch the same line.
    /// </summary>
    public static IServiceCollection AddFromSoftBase(this IServiceCollection services)
    {
        services.AddSettings<FromSoftSettings>();

        // The co-op password lives in the secret store; the step moves one left in the old settings file
        services.AddSecretKeys(FromSoftSecretKeys.CoopPassword)
            .AddSingleton<ISecretMigrationStep, CoopPasswordMigration>();
        services.AddVerb(() => CoopPasswordVerb.CoopPassword);

        // Emitters
        services.AddSingleton<PackageCollisionEmitter>()
            .AddSingleton<IFromSoftDiagnosticEmitter>(provider => provider.GetRequiredService<PackageCollisionEmitter>())
            .AddSingleton<IFromSoftDiagnosticEmitter, MergeDiagnosticsEmitter>()
            .AddSingleton<IFromSoftDiagnosticEmitter, HealEmitter>()
            .AddSingleton<Me3HostMissingEmitter>()
            .AddSingleton<IFromSoftDiagnosticEmitter>(provider => provider.GetRequiredService<Me3HostMissingEmitter>())
            .AddSingleton<UpperdirAuditEmitter>()
            .AddSingleton<IFromSoftDiagnosticEmitter>(provider => provider.GetRequiredService<UpperdirAuditEmitter>())
            .AddSingleton<IFromSoftDiagnosticEmitter, GameFolderArtifactsEmitter>()
            .AddSingleton<IFromSoftDiagnosticEmitter, SpawnerEmitter>()
            .AddSingleton<IFromSoftDiagnosticEmitter, OverlaySupportEmitter>()
            .AddSingleton<IFromSoftDiagnosticEmitter, SaveWrittenByModdedSessionEmitter>()
            .AddSingleton<Me3VersionProbe>()
            .AddSingleton<IInstallationCheck, FromSoftInstallationCheck>();

        // Providing me3 for a loadout
        services.AddSingleton<Me3HostProvisioner>()
            .AddSingleton<Me3AutoInstaller>()
            .AddSingleton<IHostedService>(provider => provider.GetRequiredService<Me3AutoInstaller>());

        // Vanilla regulations kept per build
        services.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<ISettingsManager>();
            var fileSystem = provider.GetRequiredService<IFileSystem>();
            return new RegulationBaselines(
                () => settings.Get<FromSoftSettings>().DataRoot.ToPath(fileSystem) / "baselines",
                provider.GetRequiredService<IManifestVouching>(),
                provider.GetService<TimeProvider>(),
                provider.GetRequiredService<ILogger<RegulationBaselines>>());
        });
        services.AddSingleton<IManifestVouching>(provider => new SteamManifestVouching(
            provider.GetService<IDepotManifestSource>(),
            provider.GetRequiredService<ILogger<SteamManifestVouching>>()));

        // What the merge index and planner read from a loadout
        services.AddSingleton<InstalledBuilds>()
            .AddSingleton<VouchedRegulations>()
            .AddSingleton<IVouchedRegulations>(provider => provider.GetRequiredService<VouchedRegulations>())
            .AddSingleton<VanillaDumps>()
            .AddSingleton<LoadoutMergeSources>()
            .AddSingleton<MergePlanCache>()
            .AddSingleton<IMergeReports, MergeReports>();

        // Read-only commands over the merges; the apply does the merging
        services.AddVerb(() => MergeVerbs.List)
            .AddModule("fromsoft", "Commands for the FromSoft games' loadouts")
            .AddModule("fromsoft merge", "What the app merges for a loadout");

        // Planning a game update's rebase and keeping the new baseline
        services.AddVerb(() => HealVerbs.Heal);

        // Keeping regulations Steam shipped as the baselines of their builds
        services.AddVerb(() => VanillaVerbs.Import)
            .AddModule("fromsoft vanilla", "The vanilla regulations the app keeps");

        // Launch gates, in run order
        services.AddSingleton<IExecutableLocator, PathExecutableLocator>()
            .AddSingleton<ISandboxProbe, BwrapSandboxProbe>()
            .AddSingleton<IUserIds, LibcUserIds>()
            .AddSingleton<ILaunchGate, OverlayLaunchGate>()
            .AddSingleton<ILaunchGate, VanillaSaveGate>();

        // Guided cleanup of a game folder another mod manager left
        services.AddCleanupVerbs();

        // Registrations owned by other folders
        services.AddFromSoftSaves();
        services.AddFromSoftBuild();

        return services;
    }
}
