using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries;

[UsedImplicitly]
public partial class SettingSliderControl : ReactiveUserControl<ISettingSliderViewModel>
{
    public SettingSliderControl()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not { } viewModel) return;
            var options = viewModel.DoubleContainer.Options;
            ValueSlider.Minimum = options.Minimum;
            ValueSlider.Maximum = options.Maximum;
            ValueSlider.TickFrequency = options.TickFrequency;

            this.Bind(ViewModel, vm => vm.DoubleContainer.CurrentValue, view => view.ValueSlider.Value)
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.ValueText, view => view.ValueTextBlock.Text)
                .DisposeWith(disposables);
        });
    }
}
