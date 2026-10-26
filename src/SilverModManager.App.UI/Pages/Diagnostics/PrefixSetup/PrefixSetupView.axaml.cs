using System.Linq.Expressions;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using ReactiveUI;
using SilverModManager.App.UI.Resources;

namespace SilverModManager.App.UI.Pages.Diagnostics.PrefixSetup;

public partial class PrefixSetupView : ReactiveUserControl<IPrefixSetupViewModel>
{
    public PrefixSetupView()
    {
        InitializeComponent();

        IntroText.Text = Language.PrefixSetup_Intro;
        PrefixLabel.Text = Language.PrefixSetup_PrefixLabel;
        BackupLabel.Text = Language.PrefixSetup_BackupLabel;
        RunningText.Text = Language.PrefixSetup_Running;
        DoneText.Text = Language.PrefixSetup_Done;
        ButtonRun.Text = Language.PrefixSetup_Run;
        ButtonRetry.Text = Language.PrefixSetup_Retry;
        ButtonClose.Text = Language.PrefixSetup_Close;

        // The view model is set after construction, so each watch waits for it.
        IObservable<TValue> Watch<TValue>(Expression<Func<IPrefixSetupViewModel, TValue>> property)
            => this.WhenAnyValue(view => view.ViewModel).WhereNotNull().Select(vm => vm.WhenAnyValue(property)).Switch();

        this.WhenActivated(d =>
        {
            Watch(vm => vm.GameName)
                .Subscribe(name => TitleText.Text = string.Format(Language.PrefixSetup_Title, name))
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.PrefixPath, view => view.PrefixText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.BackupPath, view => view.BackupText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.SharedNote, view => view.SharedNoteText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Steps, view => view.StepsList.ItemsSource).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ConfirmationText, view => view.ConfirmBox.Content).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.IsConfirmed, view => view.ConfirmBox.IsChecked).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Progress, view => view.ProgressList.ItemsSource).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ErrorMessage, view => view.ErrorText.Text).DisposeWith(d);

            // Rows with nothing to say are collapsed so the dialog carries no blank lines.
            Watch(vm => vm.BackupPath)
                .Subscribe(path => BackupLabel.IsVisible = BackupText.IsVisible = !string.IsNullOrEmpty(path))
                .DisposeWith(d);
            Watch(vm => vm.SharedNote)
                .Subscribe(text => SharedNoteText.IsVisible = !string.IsNullOrEmpty(text))
                .DisposeWith(d);
            Watch(vm => vm.ErrorMessage)
                .Subscribe(text => ErrorText.IsVisible = !string.IsNullOrEmpty(text))
                .DisposeWith(d);
            Watch(vm => vm.IsRunning)
                .Subscribe(running => RunningText.IsVisible = running)
                .DisposeWith(d);
            Watch(vm => vm.Succeeded)
                .Subscribe(done => DoneText.IsVisible = done)
                .DisposeWith(d);
            // Retry takes Run's place after a failure.
            Watch(vm => vm.HasFailed)
                .Subscribe(failed =>
                {
                    ButtonRetry.IsVisible = failed;
                    ButtonRun.IsVisible = !failed;
                })
                .DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.RunCommand, view => view.ButtonRun).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.RetryCommand, view => view.ButtonRetry).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.CloseCommand, view => view.ButtonClose).DisposeWith(d);
        });
    }
}
