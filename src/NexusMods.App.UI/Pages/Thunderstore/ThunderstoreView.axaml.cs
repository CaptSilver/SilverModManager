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

        if (package.PackageUrl is not null)
        {
            var link = new Button { Content = "Open on thunderstore.io", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            link.Click += (_, _) => viewModel.OpenPackageCommand.Execute(package).Subscribe();
            panel.Children.Add(link);
        }

        return panel;
    }
}
