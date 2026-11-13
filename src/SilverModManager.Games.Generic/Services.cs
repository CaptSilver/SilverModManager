using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Games.Generic.Diagnostics;
using SilverModManager.Games.Generic.FileAnalyzers;

namespace SilverModManager.Games.Generic;

public static class Services
{
    public static IServiceCollection AddGenericGameSupport(this IServiceCollection services)
    {
        services.AddSingleton<IniAnalysisData>();
        services.AddSingleton<ProtonPrefixEmitter>();
        services.AddSingleton<WineDllOverrideEmitter>();
        services.AddSingleton<WinePrefixComponentsEmitter>();
        services.AddSingleton<INativeImageReader, NativeImageReader>();
        services.AddSingleton<GameRootLibraries>();
        services.AddSingleton<IOpenXrConfig, XdgOpenXrConfig>();
        services.AddSingleton<OldVcRuntimeEmitter>();
        services.AddSingleton<OpenXrRuntimeEmitter>();

        // Shared, so every game's mapped save and settings folders are checked and a game only declares extra folders
        // (IGameUserDataFolders) rather than having to list the emitter.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticEmitter, UserDataLinkEmitter>());

        return services;
    }
}
