using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.FromSoft.Build;
using NexusMods.Games.FromSoft.Cleanup;
using NexusMods.Games.FromSoft.Diagnostics;
using NexusMods.Games.FromSoft.Doctor;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Games.FromSoft.Launch;
using NexusMods.Games.FromSoft.Overlay;
using NexusMods.Games.FromSoft.Saves;
using NexusMods.Platform.Proton.Doctor;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Settings;

namespace NexusMods.Games.FromSoft;

public static class Services
{
    /// <summary>
    /// Registers the FromSoft games. Each block takes the lines of one kind of registration, so two changes that
    /// add to different blocks never touch the same line.
    /// </summary>
    public static IServiceCollection AddFromSoftGames(this IServiceCollection services)
    {
        services.AddGame<EldenRingGame>()
            .AddSettings<FromSoftSettings>()
            .AddSingleton<ITool, RunEldenRingGame>();

        // Emitters
        services.AddSingleton<PackageCollisionEmitter>()
            .AddSingleton<IEldenRingDiagnosticEmitter>(provider => provider.GetRequiredService<PackageCollisionEmitter>())
            .AddSingleton<Me3HostMissingEmitter>()
            .AddSingleton<IEldenRingDiagnosticEmitter>(provider => provider.GetRequiredService<Me3HostMissingEmitter>())
            .AddSingleton<UpperdirAuditEmitter>()
            .AddSingleton<IEldenRingDiagnosticEmitter>(provider => provider.GetRequiredService<UpperdirAuditEmitter>())
            .AddSingleton<IEldenRingDiagnosticEmitter, GameFolderArtifactsEmitter>()
            .AddSingleton<IEldenRingDiagnosticEmitter, SpawnerEmitter>()
            .AddSingleton<IEldenRingDiagnosticEmitter, OverlaySupportEmitter>()
            .AddSingleton<IEldenRingDiagnosticEmitter, SaveWrittenByModdedSessionEmitter>()
            .AddSingleton<Me3VersionProbe>()
            .AddSingleton<IInstallationCheck, EldenRingInstallationCheck>();

        // Launch gates, in run order
        services.AddSingleton<IExecutableLocator, PathExecutableLocator>()
            .AddSingleton<ISandboxProbe, BwrapSandboxProbe>()
            .AddSingleton<IUserIds, LibcUserIds>()
            .AddSingleton<ILaunchGate, OverlayLaunchGate>()
            .AddSingleton<ILaunchGate, VanillaSaveGate>();

        // Guided cleanup of a game folder another mod manager left
        services.AddCleanupVerbs();

        // Registrations owned by other folders
        services.AddEldenRingSaves();
        services.AddEldenRingBuild();

        return services;
    }
}
