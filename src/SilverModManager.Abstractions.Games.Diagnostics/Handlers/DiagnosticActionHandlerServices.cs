using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Games;

namespace SilverModManager.Abstractions.Diagnostics;

public static class DiagnosticActionHandlerServices
{
    /// <summary>
    /// Registers <typeparamref name="THandler"/> once, however many graphs and call sites ask for it.
    /// </summary>
    public static IServiceCollection AddDiagnosticActionHandler<THandler>(this IServiceCollection services)
        where THandler : class, IDiagnosticActionHandler
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDiagnosticActionHandler, THandler>());
        return services;
    }

    /// <summary>
    /// The handlers that need no window and ask nothing, so the command line can run them with the app closed. The ones
    /// that open a dialog stay with the window.
    /// </summary>
    public static IServiceCollection AddUnattendedDiagnosticActionHandlers(this IServiceCollection services)
    {
        return services
            .AddFreedesktopTrash()
            .AddDiagnosticActionHandler<DismissGameMovedActionHandler>()
            .AddDiagnosticActionHandler<DismissDiagnosticSubjectsActionHandler>()
            .AddDiagnosticActionHandler<VerifyGameFilesInSteamActionHandler>()
            .AddDiagnosticActionHandler<MoveHostFolderIntoGameActionHandler>();
    }
}
