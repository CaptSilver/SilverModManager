using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.Thunderstore;

[UsedImplicitly]
public partial class ThunderstoreView : ReactiveUserControl<IThunderstoreViewModel>
{
    public ThunderstoreView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;

            this.BindCommand(ViewModel, vm => vm.RefreshCommand, view => view.RefreshButton).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SearchText, view => view.SearchBox.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ShowDeprecated, view => view.DeprecatedBox.IsChecked).DisposeWith(disposables);

            viewModel.WhenAnyValue(vm => vm.ErrorText)
                .Subscribe(text =>
                {
                    ErrorText.Text = text;
                    ErrorText.IsVisible = text != "";
                })
                .DisposeWith(disposables);

            // Rows are rebuilt as a whole on every filter change; the list is capped by what a person scrolls through.
            viewModel.WhenAnyValue(vm => vm.Packages)
                .Subscribe(packages => PackageList.ItemsSource = packages.Select(package => Row(viewModel, package)).ToArray())
                .DisposeWith(disposables);
        });
    }

    private static Control Row(IThunderstoreViewModel viewModel, ThunderstorePackageRow package)
    {
        var title = $"{package.FullName}  {package.LatestVersion}  (rating {package.Rating:0})" + (package.IsDeprecated ? "  [deprecated]" : "");
        var panel = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(0, 4) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        if (package.Description != "") panel.Children.Add(new TextBlock { Text = package.Description, TextWrapping = TextWrapping.Wrap });

        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        if (package.Latest is not null)
        {
            var install = new Button { Content = "Install" };
            // Through ICommand, which honours CanExecute: the command is disabled while it runs, so a double click can't start a second install.
            install.Click += (_, _) => ((System.Windows.Input.ICommand)viewModel.InstallCommand).Execute(package);
            buttons.Children.Add(install);
        }

        if (package.PackageUrl is not null)
        {
            var link = new Button { Content = "Open on thunderstore.io" };
            link.Click += (_, _) => viewModel.OpenPackageCommand.Execute(package).Subscribe();
            buttons.Children.Add(link);
        }

        if (buttons.Children.Count > 0) panel.Children.Add(buttons);

        return panel;
    }
}
