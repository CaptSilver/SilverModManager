using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.ReactiveUI;
using SilverModManager.App.UI.Extensions;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using static SilverModManager.App.UI.Helpers.StyleConstants.SpineDownloadButton;

namespace SilverModManager.App.UI.Controls.Spine.Buttons.Download;

public partial class SpineDownloadButtonView : ReactiveUserControl<ISpineDownloadButtonViewModel>
{
    public SpineDownloadButtonView()
    {
        InitializeComponent();

        this.WhenActivated(d =>
        {
            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.Click))
                .BindToUi(this, view => view.ParentButton.Command)
                .DisposeWith(d);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.Progress))
                .Select(p => p.HasValue)
                .BindToClasses(ParentButton, Progress, Idle)
                .DisposeWith(d);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.IsActive))
                .BindToActive(ParentButton)
                .DisposeWith(d);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.Progress))
                .Where(p => p.HasValue)
                .Select(p => p.Value.Value * 360)
                .BindToUi(this, vm => vm.ProgressArc.SweepAngle)
                .DisposeWith(d);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.Number))
                .Select(n => n.ToString("###0.0"))
                .BindToUi(this, view => view.NumberTextBlock.Text)
                .DisposeWith(d);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(x => x.Units))
                .Select(n => n.ToUpperInvariant())
                .BindToUi(this, view => view.UnitsTextBlock.Text)
                .DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.ToolTip,
                    v => v.ToolTipTextBlock.Text)
                .DisposeWith(d);
        });
    }
}
