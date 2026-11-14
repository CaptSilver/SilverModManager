using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;

namespace SilverModManager.App.UI.Input.Menu;

public partial class AppMenuOverlayView : ReactiveUserControl<IAppMenuOverlayViewModel>
{
    public AppMenuOverlayView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.Items, view => view.MenuItems.ItemsSource).DisposeWith(disposables);
            MenuItems.AddHandler(Button.ClickEvent, OnItemClicked);
            Disposable.Create(() => MenuItems.RemoveHandler(Button.ClickEvent, OnItemClicked)).DisposeWith(disposables);

            // A pad has no pointer to click into the menu, so the focus has to start inside it.
            Dispatcher.UIThread.Post(FocusFirstEntry, DispatcherPriority.Loaded);
        });
    }

    private void FocusFirstEntry() =>
        this.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyEnabled)?.Focus(NavigationMethod.Directional);

    private void OnItemClicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { DataContext: AppMenuItem item }) ViewModel?.Choose(item);
    }
}
