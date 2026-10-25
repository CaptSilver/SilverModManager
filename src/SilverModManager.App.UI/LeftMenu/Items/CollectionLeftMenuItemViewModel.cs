using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls.Navigation;
using SilverModManager.App.UI.Helpers;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.LeftMenu.Items;

public partial class CollectionLeftMenuItemViewModel : LeftMenuItemViewModel, ILeftMenuItemWithToggleViewModel
{
    [Reactive] public partial bool IsEnabled { get; set; }

    public bool IsCollectionReadOnly { get; init; }

    /// <summary>
    /// The loadout's own "My Mods" collection: it stays first in the menu and has no Move up / Move down entries.
    /// See <see cref="LeftMenu.Loadout.LoadoutCollectionOrder.DefaultCollectionOf"/>.
    /// </summary>
    public bool IsDefaultCollection { get; init; }
    public bool IsToggleVisible => true;

    public ReactiveCommand<Unit, Unit> ToggleIsEnabledCommand { get; }
    
    public CollectionGroupId CollectionGroupId { get; }

    private readonly IServiceProvider _serviceProvider;
    private readonly IConnection _connection;
    private readonly IWorkspaceController _workspaceController;
    private readonly bool _isNexusCollection;
    private readonly IWindowNotificationService _toastNotificationService;
    private readonly IWindowManager _windowManager;

    public CollectionLeftMenuItemViewModel(
        IWorkspaceController workspaceController,
        WorkspaceId workspaceId,
        PageData pageData,
        IServiceProvider serviceProvider,
        CollectionGroupId collectionGroupId,
        bool isDefaultCollection = false,
        Action<CollectionGroupId, int>? moveBy = null) : base(workspaceController, workspaceId, pageData)
    {
        _serviceProvider = serviceProvider;
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _toastNotificationService = serviceProvider.GetRequiredService<IWindowNotificationService>();
        _windowManager = serviceProvider.GetRequiredService<IWindowManager>();
        _workspaceController = workspaceController;

        CollectionGroupId = collectionGroupId;
        IsDefaultCollection = isDefaultCollection;

        // Detect collection type and create delete context menu item
        var collectionGroup = CollectionGroup.Load(_connection.Db, CollectionGroupId);
        _isNexusCollection = collectionGroup.TryGetAsNexusCollectionLoadoutGroup(out _);
        var deleteContextMenuItem = CreateDeleteContextMenuItem();

        var isEnabledObservable = CollectionGroup.Observe(_connection, collectionGroupId)
            .Select(collGroup => collGroup.AsLoadoutItemGroup().AsLoadoutItem().IsEnabled());
        
        ToggleIsEnabledCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            using var tx = _connection.BeginTransaction();
            
            if (IsEnabled)
            {
                tx.Retract(CollectionGroupId.Value, LoadoutItem.Disabled, Null.Instance);
            } else
            {
                tx.Add(CollectionGroupId.Value, LoadoutItem.Disabled, Null.Instance);
            }
            
            await tx.Commit();
        });
        
        // Set additional context menu items
        AdditionalContextMenuItems = isDefaultCollection || moveBy is null
            ? [deleteContextMenuItem]
            : [CreateMoveContextMenuItem(Language.SpineLoadoutMenu_MoveUp, IconValues.ArrowUp, () => moveBy(collectionGroupId, -1)),
               CreateMoveContextMenuItem(Language.SpineLoadoutMenu_MoveDown, IconValues.ArrowDown, () => moveBy(collectionGroupId, 1)),
               deleteContextMenuItem];
        
        this.WhenActivated(d =>
        {
            isEnabledObservable
                .OnUI()
                .Subscribe(isEnabled => IsEnabled = isEnabled)
                .DisposeWith(d);
        });

    }
    
    private static IContextMenuItem CreateMoveContextMenuItem(string header, IconValue icon, Action move) => new ContextMenuItem
    {
        Header = header,
        Icon = icon,
        Command = ReactiveCommand.Create(move),
    };

    private IContextMenuItem CreateDeleteContextMenuItem()
    {
        var deleteCommand = CreateDeleteCommand();
        
        var header = _isNexusCollection 
            ? Language.CollectionLoadoutView_UninstallCollection 
            : Language.Loadout_DeleteCollection_Menu_Text;
            
        var icon = _isNexusCollection 
            ? IconValues.PlaylistRemove 
            : IconValues.DeleteOutline;

        var styling = _isNexusCollection
            ? ContextMenuItemStyling.Default // Note(sewer): This is not red because it can be undone; the action that comes after.
                                             //              Deleting collection revisions is the dangerous one and has the red marker. 
            : ContextMenuItemStyling.Critical;
        
        return new ContextMenuItem
        {
            Header = header,
            Icon = icon,
            Command = deleteCommand,
            IsVisible = true,
            Styling = styling,
        };
    }
    
    private ReactiveCommand<Unit, Unit> CreateDeleteCommand()
    {
        // Nexus collections can always be uninstalled, regular collections follow CanDeleteCollection logic
        var canExecute = _isNexusCollection 
            ? Observable.Return(true)
            : CollectionDeleteHelpers.ObserveCanDeleteCollection(CollectionGroupId, _connection);

        return ReactiveCommand.CreateFromTask(async () =>
        {
            var collectionName = LoadoutItem.Load(_connection.Db, CollectionGroupId).Name;
            var confirmed = await CollectionDeleteHelpers.ShowDeleteConfirmationDialogAsync(collectionName, _windowManager);
            
            if (confirmed)
            {
                await CollectionDeleteHelpers.DeleteCollectionAsync(
                    CollectionGroupId,
                    _serviceProvider.GetRequiredService<ILoadoutManager>(),
                    _workspaceController,
                    _connection,
                    _toastNotificationService);
            }
        }, canExecute: canExecute);
    }
}
