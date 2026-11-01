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
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
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

    private readonly IConnection _connection;

    /// <summary>
    /// An installed collection's entry. What it opens and how it is removed come from <paramref name="source"/>; the
    /// toggle and the order are the same for every kind of collection.
    /// </summary>
    public CollectionLeftMenuItemViewModel(
        IWorkspaceController workspaceController,
        WorkspaceId workspaceId,
        CollectionSource source,
        IServiceProvider serviceProvider,
        CollectionGroupId collectionGroupId,
        bool isDefaultCollection = false,
        Action<CollectionGroupId, int>? moveBy = null) : base(workspaceController, workspaceId, source.InstalledPage(collectionGroupId))
    {
        _connection = serviceProvider.GetRequiredService<IConnection>();

        CollectionGroupId = collectionGroupId;
        IsDefaultCollection = isDefaultCollection;

        var deleteContextMenuItem = source.InstalledRemoval(collectionGroupId)
            .ToMenuItem(serviceProvider.GetRequiredService<IWindowManager>(), workspaceController);

        var isEnabledObservable = CollectionGroup.Observe(_connection, collectionGroupId)
            .Select(collGroup => collGroup.AsLoadoutItemGroup().AsLoadoutItem().IsEnabled());
        
        ToggleIsEnabledCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            using var tx = _connection.BeginTransaction();
            
            if (IsEnabled)
            {
                tx.RetractCurrent(CollectionGroupId.Value, LoadoutItem.Disabled);
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
}
