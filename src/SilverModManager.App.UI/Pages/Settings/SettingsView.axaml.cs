using System.Globalization;
using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Settings;

public partial class SettingsView : ReactiveUserControl<ISettingsPageViewModel>
{
    private ISettingsPageViewModel? _builtFor;
    private readonly CompositeDisposable _badges = new();

    public SettingsView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
            {
                this.BindCommand(ViewModel, viewModel => viewModel.SaveCommand, view => view.SaveButton)
                    .DisposeWith(d);

                this.BindCommand(ViewModel,viewModel => viewModel.CancelCommand,view => view.CancelButton)
                    .DisposeWith(d);

                this.WhenAnyValue(v => v.ViewModel)
                    .WhereNotNull()
                    .Subscribe(vm =>
                    {
                        // WhenActivated replays the view model every time the view re-attaches; rebuilding would reset the
                        // selected tab and stack another badge subscription, so build the strip once per view model.
                        if (ReferenceEquals(_builtFor, vm)) return;
                        _builtFor = vm;
                        _badges.Clear();
                        Tabs.ItemsSource = vm.Tabs.Select(TabItemFor).ToArray();
                    })
                    .DisposeWith(d);
            }
        );
    }

    // Built in code, not from a HeaderTemplate: TabControlStyles.axaml styles "TabItem > StackPanel" and its Border badge.
    private TabItem TabItemFor(ISettingsTabViewModel tab)
    {
        var badgeText = new TextBlock();
        var badge = new Border { Child = badgeText };
        var header = new StackPanel
        {
            Children =
            {
                new UnifiedIcon { Value = tab.Icon },
                new TextBlock { Text = tab.Name },
                badge,
            },
        };

        _badges.Add(R3.ObservableSubscribeExtensions.Subscribe(tab.ChangedCount, count =>
        {
            badge.IsVisible = count > 0;
            badgeText.Text = count.ToString(CultureInfo.InvariantCulture);
        }));

        return new TabItem { Header = header, Content = new SettingsTabView { ViewModel = tab } };
    }
}
