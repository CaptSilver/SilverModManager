using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries;

[UsedImplicitly]
public partial class SettingActionButtonControl : ReactiveUserControl<ISettingActionButtonViewModel>
{
    public SettingActionButtonControl()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.Text, view => view.StatusText.Text)
                .DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.ButtonLabel, view => view.ActionButton.Content)
                .DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.HasButton, view => view.ActionButton.IsVisible)
                .DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.InvokeCommand, view => view.ActionButton)
                .DisposeWith(disposables);
        });
    }
}
