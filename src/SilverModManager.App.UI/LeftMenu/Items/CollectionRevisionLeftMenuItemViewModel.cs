using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Helpers;
using SilverModManager.App.UI.Pages.CollectionDownload;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;

namespace SilverModManager.App.UI.LeftMenu.Items;

public class CollectionRevisionLeftMenuItemViewModel : LeftMenuItemWithRightIconViewModel
{
    private readonly CollectionRevisionMetadataId _collectionRevisionMetadataId;
    private readonly CollectionDownloader _collectionDownloader;
    private readonly IWindowNotificationService _notificationService;
    private readonly IWindowManager _windowManager;
    private readonly IConnection _connection;
    private readonly IWorkspaceController _workspaceController;

    public CollectionRevisionLeftMenuItemViewModel(
        IWorkspaceController workspaceController,
        WorkspaceId workspaceId,
        PageData pageData,
        CollectionRevisionMetadata.ReadOnly revision,
        IServiceProvider serviceProvider) : base(workspaceController, workspaceId, pageData)
    {
        _workspaceController = workspaceController;
        _collectionRevisionMetadataId = revision;
        _collectionDownloader = serviceProvider.GetRequiredService<CollectionDownloader>();
        _notificationService = serviceProvider.GetRequiredService<IWindowNotificationService>();
        _windowManager = serviceProvider.GetRequiredService<IWindowManager>();
        _connection = serviceProvider.GetRequiredService<IConnection>();

        AdditionalContextMenuItems =
        [
            CreateDeleteContextMenuItem(),
        ];
    }

    private IContextMenuItem CreateDeleteContextMenuItem()
    {
        var deleteCommand = CreateDeleteCommand();
        
        return new ContextMenuItem
        {
            Header = Language.CollectionDownloadView_Menu_DeleteCollectionRevision,
            Icon = IconValues.DeleteOutline,
            Command = deleteCommand,
            IsVisible = true,
            Styling = ContextMenuItemStyling.Critical,
        };
    }
    
    private ReactiveCommand<Unit, Unit> CreateDeleteCommand()
    {
        // Non-installed collection revisions can always be deleted
        var canExecute = Observable.Return(true);

        return ReactiveCommand.CreateFromTask(async () =>
        {
            var revision = CollectionRevisionMetadata.Load(_connection.Db, _collectionRevisionMetadataId);
            
            var confirmed = await CollectionDeleteHelpers.ShowDeleteConfirmationDialogAsync(
                revision.Collection.Name, _windowManager);
            
            if (!confirmed)
                return;

            try
            {
                await _collectionDownloader.DeleteRevision(_collectionRevisionMetadataId);
                _notificationService.ShowToast(
                    Language.ToastNotification_Collection_removed,
                    ToastNotificationVariant.Success
                );

                // Navigate away from the deleted revision page to default new tab
                await Dispatcher.UIThread.InvokeAsync(() =>
                    _workspaceController.ReplacePages<CollectionDownloadPageContext>(
                    context => context.CollectionRevisionMetadataId == _collectionRevisionMetadataId)
                );
            }
            catch (Exception ex)
            {
                _notificationService.ShowToast(
                    $"Failed to delete collection: {ex.Message}",
                    ToastNotificationVariant.Failure
                );
            }
        }, canExecute: canExecute);
    }
}
