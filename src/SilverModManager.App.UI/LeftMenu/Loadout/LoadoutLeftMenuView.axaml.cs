using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Resources;
using SilverModManager.Collections;
using ReactiveUI;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

[UsedImplicitly]
public partial class LoadoutLeftMenuView : ReactiveUserControl<ILoadoutLeftMenuViewModel>
{
    public LoadoutLeftMenuView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.ApplyControlViewModel, view => view.ApplyControlViewHost.ViewModel)
                .DisposeWith(disposables);
            
            this.OneWayBind(ViewModel, vm => vm.LeftMenuItemLibrary, view => view.LibraryItem.ViewModel)
                .DisposeWith(disposables);
            
            this.OneWayBind(ViewModel, vm => vm.LeftMenuItemLoadout, view => view.LoadoutItem.ViewModel)
                .DisposeWith(disposables);

            this.WhenAnyValue(x => x.ViewModel)
                .WhereNotNull()
                .Select(vm => vm.LeftMenuStoreItems)
                .Do(items => StoresSection.IsVisible = items.Count > 0)
                .BindTo(this, x => x.StoreItems.ItemsSource)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.LeftMenuItemNewCollection, view => view.NewCollection.ViewModel)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.LeftMenuItemHealthCheck, view => view.HealthCheckItem.ViewModel)
                .DisposeWith(disposables);
            
            this.OneWayBind(ViewModel, vm => vm.LeftMenuItemTools, view => view.ToolsItem.ViewModel)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.LeftMenuItemExternalChanges, view => view.ExternalChangesItem.ViewModel)
                .DisposeWith(disposables);
            
            this.WhenAnyValue(view => view.ViewModel!.LeftMenuItemExternalChanges)
                .Select(item => item != null)
                .BindTo(this, view => view.ExternalChangesItem.IsVisible)
                .DisposeWith(disposables);

            this.WhenAnyValue(x => x.ViewModel!.LeftMenuCollectionItems)
                .BindTo(this, x => x.CollectionItems.ItemsSource)
                .DisposeWith(disposables);
            
            // Dragging an installed collection reorders the list the menu is bound to; the view model decides what may move.
            var reorderDrag = new SerialDisposable().DisposeWith(disposables);
            this.WhenAnyValue(x => x.ViewModel)
                .WhereNotNull()
                .Subscribe(vm => reorderDrag.Disposable = new VerticalReorderDrag(CollectionItems, LeftMenuScroller, vm.IsCollectionMovable, vm.MoveCollection))
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.HasSingleCollection, view => view.LoadoutItem.IsVisible, input => !input)
                .DisposeWith(disposables);
            
            StoresSectionText.Text = Language.LeftMenu_Label_Stores;
            InstalledModsSectionText.Text = Language.LeftMenu_Label_Installed_Mods;
            UtilitiesSectionText.Text = Language.LeftMenu_Label_Utilities;
        });
    }
}
