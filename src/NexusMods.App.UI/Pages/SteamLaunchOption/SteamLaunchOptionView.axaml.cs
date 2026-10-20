using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.SteamLaunchOption;

public partial class SteamLaunchOptionView : ReactiveUserControl<ISteamLaunchOptionViewModel>
{
    public SteamLaunchOptionView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
        {
            this.WhenAnyValue(view => view.ViewModel!.LoadoutName)
                .Subscribe(name => TitleText.Text = $"Use \"{name}\" from Steam")
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.Line, view => view.LineText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.CopyNote, view => view.CopyNoteText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.WriteNote, view => view.WriteNoteText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Message, view => view.MessageText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Warning, view => view.WarningText.Text).DisposeWith(d);

            // Collapsed when empty so the dialog does not carry blank rows.
            this.WhenAnyValue(view => view.ViewModel!.Warning)
                .Subscribe(text => WarningText.IsVisible = !string.IsNullOrEmpty(text))
                .DisposeWith(d);
            this.WhenAnyValue(view => view.ViewModel!.WriteNote)
                .Subscribe(text => WriteNoteText.IsVisible = !string.IsNullOrEmpty(text))
                .DisposeWith(d);
            this.WhenAnyValue(view => view.ViewModel!.Message)
                .Subscribe(text => MessageText.IsVisible = !string.IsNullOrEmpty(text))
                .DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.CopyCommand, view => view.ButtonCopy).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.WriteCommand, view => view.ButtonWrite).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.CloseCommand, view => view.ButtonClose).DisposeWith(d);
        });
    }
}
