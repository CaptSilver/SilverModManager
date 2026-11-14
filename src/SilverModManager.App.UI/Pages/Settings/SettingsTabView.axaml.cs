using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia;
using SilverModManager.App.UI.Controls.Settings.SettingEntries;
using SilverModManager.App.UI.Input;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Settings;

public partial class SettingsTabView : ReactiveUserControl<ISettingsTabViewModel>
{
    public SettingsTabView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
        {
            this.OneWayBind(ViewModel, vm => vm.Items, view => view.SettingEntriesItemsControl.ItemsSource)
                .DisposeWith(d);
        });
    }

    private TopLevel? _root;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _root = TopLevel.GetTopLevel(this);
        // Tunnel on the window: a pad press is raised on the window itself, not on the focused row, and the window's
        // own directional navigation runs on the way back up, so a handler lower down never sees it in time.
        _root?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _root?.RemoveHandler(KeyDownEvent, OnKeyDown);
        _root = null;
        base.OnDetachedFromVisualTree(e);
    }

    // Directional focus from the first row would pick whichever header is nearest, and a focused header selects its
    // tab. Send Up to the header of this tab instead, so reaching the strip never changes the tab.
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Up || e.KeyModifiers != KeyModifiers.None) return;
        if (ViewModel?.Entries.FirstOrDefault() is not { } first) return;

        var focused = _root?.FocusManager?.GetFocusedElement();
        if (!FocusAncestors.Of(focused).Contains(this)) return;
        if (FocusAncestors.Nearest<SettingEntryView>(focused) is not { } row || !ReferenceEquals(row.ViewModel, first)) return;
        if (PadFocusScopes.For(focused)?.Scope.WantsDirection(NavigationDirection.Up) == true) return;
        if (FocusAncestors.Nearest<TabControl>(this) is not { } tabs) return;

        tabs.ContainerFromIndex(tabs.SelectedIndex)?.Focus(NavigationMethod.Directional);
        e.Handled = true;
    }
}
