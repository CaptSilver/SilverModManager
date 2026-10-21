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
            this.BindCommand(ViewModel, vm => vm.CloseAndWriteCommand, view => view.ButtonCloseAndWrite).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.CancelWaitCommand, view => view.ButtonCancelWait).DisposeWith(d);

            // Close-and-write takes the place of Write while Steam runs; Cancel shows only during the wait.
            this.WhenAnyValue(view => view.ViewModel)
                .WhereNotNull()
                .Select(vm => vm.WhenAnyValue(x => x.IsSteamRunning, x => x.IsWaitingForSteam, (running, waiting) => (running, waiting)))
                .Switch()
                .Subscribe(state =>
                {
                    ButtonCloseAndWrite.IsVisible = state.running && !state.waiting;
                    ButtonWrite.IsVisible = !state.running && !state.waiting;
                    ButtonCancelWait.IsVisible = state.waiting;
                })
                .DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.OldLine, view => view.OldLineText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.NewLine, view => view.NewLineText.Text).DisposeWith(d);
            this.WhenAnyValue(view => view.ViewModel!.OtherManagerName)
                .Subscribe(name => ReplaceIntroText.Text = string.IsNullOrEmpty(name)
                    ? ""
                    : $"Steam already holds a launch line from {name}. \"Replace it\" keeps a record of the old line and puts this loadout's line there.")
                .DisposeWith(d);
            this.WhenAnyValue(view => view.ViewModel!.CanReplace)
                .Subscribe(canReplace => ReplacePanel.IsVisible = ButtonReplace.IsVisible = canReplace)
                .DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.ReplaceCommand, view => view.ButtonReplace).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.CloseCommand, view => view.ButtonClose).DisposeWith(d);
        });
    }
}
