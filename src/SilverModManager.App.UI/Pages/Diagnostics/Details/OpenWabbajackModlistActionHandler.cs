using System.Collections.Immutable;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.App.UI.Pages.Diagnostics;

/// <summary>
/// Opens the page of the installed modlist a diagnostic is about, fixed to the diagnostic's loadout. The page is where the
/// user decides what to do about the newer version.
/// </summary>
[UsedImplicitly]
public sealed class OpenWabbajackModlistActionHandler(IServiceProvider serviceProvider) : IDiagnosticActionHandler
{
    /// <inheritdoc />
    public string ActionId => DiagnosticActions.OpenWabbajackModlistId;

    /// <inheritdoc />
    public Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
    {
        var (fileId, _) = DiagnosticActions.ParseOpenWabbajackModlist(arguments);
        if (!WabbajackModlistLibraryFile.Load(serviceProvider.GetRequiredService<IConnection>().Db, fileId).IsValid())
            throw new ModlistNotInLibraryException("The modlist is no longer in the library.");

        var page = new PageData
        {
            FactoryId = WabbajackModlistPageFactory.StaticId,
            Context = new WabbajackModlistPageContext
            {
                LibraryFileId = LibraryFileId.From(fileId),
                TargetLoadout = loadoutId,
            },
        };
        var workspaceController = serviceProvider.GetRequiredService<IWindowManager>().ActiveWorkspaceController;
        workspaceController.OpenPage(workspaceController.ActiveWorkspaceId, page,
            workspaceController.GetOpenPageBehavior(page, new NavigationInformation(NavigationInput.Default, Optional<OpenPageBehaviorType>.None)));
        return Task.CompletedTask;
    }
}

/// <summary>The modlist a diagnostic pointed at was removed from the library before the action ran.</summary>
internal sealed class ModlistNotInLibraryException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The modlist was not found";
}
