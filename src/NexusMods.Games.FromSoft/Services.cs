using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft.Build;
using NexusMods.Games.FromSoft.Cleanup;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Heal;
using NexusMods.Games.FromSoft.Launch;
using NexusMods.Games.FromSoft.Me3;
using NexusMods.Games.FromSoft.Merge;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Games.FromSoft.Saves;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Doctor;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.ProxyConsole;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft;

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
            return new RegulationBaselines(() => settings.Get<FromSoftSettings>().DataRoot.ToPath(fileSystem) / "baselines");
        });

        // What the merge index and planner read from a loadout
        services.AddSingleton<VouchedRegulations>()
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
