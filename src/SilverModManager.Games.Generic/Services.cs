using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<IOpenXrConfig, XdgOpenXrConfig>();
        services.AddSingleton<OldVcRuntimeEmitter>();
        services.AddSingleton<OpenXrRuntimeEmitter>();

        return services;
    }
}
