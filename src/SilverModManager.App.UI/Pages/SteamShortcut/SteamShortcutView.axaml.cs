using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using ReactiveUI;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Pages.SteamShortcut;

public partial class SteamShortcutView : ReactiveUserControl<ISteamShortcutViewModel>
{
    public SteamShortcutView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
        {
            this.OneWayBind(ViewModel, vm => vm.Title, view => view.TitleText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Note, view => view.NoteText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.AskSteamNote, view => view.AskSteamNoteText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Message, view => view.MessageText.Text).DisposeWith(d);

            // Collapsed when empty so the dialog does not carry blank rows.
            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.Note))
                .Subscribe(text => NoteText.IsVisible = !string.IsNullOrEmpty(text))
                .DisposeWith(d);
            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.Message))
                .Subscribe(text => MessageText.IsVisible = !string.IsNullOrEmpty(text))
                .DisposeWith(d);
            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.CanAskSteam))
                .Subscribe(can => AskSteamNoteText.IsVisible = ButtonAskSteam.IsVisible = can)
                .DisposeWith(d);
            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.CanAdd))
                .Subscribe(can => ButtonAdd.IsVisible = can)
                .DisposeWith(d);
            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.CanCloseAndAdd))
                .Subscribe(can => ButtonCloseAndAdd.IsVisible = can)
                .DisposeWith(d);
            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.CanRemove))
                .Subscribe(can => ButtonRemove.IsVisible = can)
                .DisposeWith(d);
            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.CanCloseAndRemove))
                .Subscribe(can => ButtonCloseAndRemove.IsVisible = can)
                .DisposeWith(d);
            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.IsWaitingForSteam))
                .Subscribe(waiting => ButtonCancelWait.IsVisible = waiting)
                .DisposeWith(d);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.Accounts))
                .Subscribe(accounts =>
                {
                    AccountPanel.IsVisible = accounts.Count > 0;
                    AccountBox.ItemsSource = accounts;
                })
                .DisposeWith(d);
            AccountBox.SelectionChanged += (_, _) =>
            {
                if (ViewModel is { } vm && AccountBox.SelectedItem is Platform.Proton.Steam.Shortcuts.SteamAccount account) vm.SelectedAccount = account;
            };

            this.BindCommand(ViewModel, vm => vm.AddCommand, view => view.ButtonAdd).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.CloseAndAddCommand, view => view.ButtonCloseAndAdd).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.AskSteamCommand, view => view.ButtonAskSteam).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.RemoveCommand, view => view.ButtonRemove).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.CloseAndRemoveCommand, view => view.ButtonCloseAndRemove).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.CancelWaitCommand, view => view.ButtonCancelWait).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.CloseCommand, view => view.ButtonClose).DisposeWith(d);
        });
    }
}
