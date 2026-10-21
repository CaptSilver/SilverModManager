using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NexusMods.App.UI.Pages.Diagnostics;

public static class DiagnosticActionServices
{
    /// <summary>
    /// Registers the handlers that give diagnostic actions their buttons.
    /// </summary>
    public static IServiceCollection AddDiagnosticActionHandlers(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, UseLoadoutFromSteamActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, RestoreFolderFromBackupActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, ShowCleanupStepsActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, RestampBuildActionHandler>());
        return services;
    }
}
