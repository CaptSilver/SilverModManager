using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.LoadoutGroupFilesPage;

[UsedImplicitly]
public partial class MoveToModDialogView : ReactiveUserControl<IMoveToModDialogViewModel>
{
    public MoveToModDialogView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;
            TextBlockText.Text = viewModel.Text;

            ModPicker.IsVisible = viewModel.Mods.Count > 0;
            ModPicker.ItemsSource = viewModel.Mods;
            ModPicker.SelectedItem = viewModel.SelectedMod;
            ModPicker.SelectionChanged += OnPicked;
            Disposable.Create(() => ModPicker.SelectionChanged -= OnPicked).DisposeWith(disposables);

            DetailsScroller.IsVisible = viewModel.Details.Length > 0;
            TextBlockDetails.Text = viewModel.Details;

            KeepWinningCheckBox.IsVisible = viewModel.CanKeepWinning;
            KeepWinningCheckBox.IsChecked = viewModel.KeepWinning;
            KeepWinningCheckBox.IsCheckedChanged += OnKeepWinningChanged;
            Disposable.Create(() => KeepWinningCheckBox.IsCheckedChanged -= OnKeepWinningChanged).DisposeWith(disposables);
        });
    }

    private void OnPicked(object? sender, Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.SelectedMod = ModPicker.SelectedItem as MoveTargetChoice;
    }

    private void OnKeepWinningChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.KeepWinning = KeepWinningCheckBox.IsChecked == true;
    }
}
