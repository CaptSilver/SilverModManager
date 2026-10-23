using System.Reactive.Disposables;
using Avalonia.ReactiveUI;
using JetBrains.Annotations;
using SilverModManager.App.UI;
using SilverModManager.App.UI.Controls;
using SilverModManager.Games.FOMOD.UI.Resources;
using ReactiveUI;

namespace SilverModManager.Games.FOMOD.UI;

[UsedImplicitly]
public partial class FooterStepperView : ReactiveUserControl<IFooterStepperViewModel>
{
    public FooterStepperView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.Progress, view => view.ProgressBar.Value, progress => progress.Value)
                .DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.GoToPrevCommand, view => view.GoToPrevButton)
                .DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.GoToNextCommand, view => view.GoToNextButton)
                .DisposeWith(disposables);

            this.WhenAnyValue(view => view.ViewModel!.IsLastStep)
                .SubscribeWithErrorLogging(isLastStep =>
                {
                    GoToNextButton.Text = isLastStep ? Language.FooterStepperView_FooterStepperView_Finish : Language.FooterStepperView_FooterStepperView_Next;
                })
                .DisposeWith(disposables);
        });
    }
}

