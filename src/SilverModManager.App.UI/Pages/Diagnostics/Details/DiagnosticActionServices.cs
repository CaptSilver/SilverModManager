using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI.Pages.Diagnostics.PrefixSetup;
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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, CheckModUpdatesActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, DisableModsActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, SetUpPrefixActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, ReapplyCollectionOrderActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, MoveHostFolderIntoGameActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, RestoreRegulationActionHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, VerifyGameFilesInSteamActionHandler>());
        services.TryAddSingleton<ITrash>(provider => new FreedesktopTrash(provider.GetRequiredService<IFileSystem>().GetKnownPath(KnownPath.XDG_DATA_HOME).GetFullPath()));
        services.TryAddSingleton<IPrefixSetupLauncher, PrefixSetupLauncher>();
        services.AddView<PrefixSetupView, IPrefixSetupViewModel>();

        // What the Fix button needs to save the old mods to the library before it deletes them
        services.TryAddSingleton<INexusCatalog, NexusCatalogAdapter>();
        services.TryAddSingleton<ILibraryProbe, LibraryProbe>();
        services.TryAddSingleton<IModDownloader, NexusModDownloader>();
        services.AddView<SaveToLibraryDialogView, ISaveToLibraryDialogViewModel>();
        return services;
    }
}
