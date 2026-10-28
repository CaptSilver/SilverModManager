using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries;

[UsedImplicitly]
public partial class SettingTextControl : ReactiveUserControl<ISettingTextViewModel>
{
    public SettingTextControl()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.Bind(ViewModel, vm => vm.TextContainer.CurrentValue, view => view.ValueTextBox.Text)
                .DisposeWith(disposables);
        });
    }
}
