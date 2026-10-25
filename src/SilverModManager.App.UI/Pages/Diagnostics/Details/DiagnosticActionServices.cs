using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.App.UI.Pages.Diagnostics.SaveToLibrary;
using SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;

namespace SilverModManager.App.UI.Pages.Diagnostics;

public static class DiagnosticActionServices
{
    /// <summary>
    /// Registers the handlers that give diagnostic actions their buttons.
    /// </summary>
    public static IServiceCollection AddDiagnosticActionHandlers(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, UseLoadoutFromSteamActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, RestoreFolderFromBackupActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, FixCleanupActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, ShowCleanupStepsActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, RestampBuildActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, InstallMe3ActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, DismissGameMovedActionHandler>());

        // What the Fix button needs to save the old mods to the library before it deletes them
        services.TryAddSingleton<INexusCatalog, NexusCatalogAdapter>();
        services.TryAddSingleton<ILibraryProbe, LibraryProbe>();
        services.TryAddSingleton<IModDownloader, NexusModDownloader>();
        services.AddView<SaveToLibraryDialogView, ISaveToLibraryDialogViewModel>();
        return services;
    }
}
