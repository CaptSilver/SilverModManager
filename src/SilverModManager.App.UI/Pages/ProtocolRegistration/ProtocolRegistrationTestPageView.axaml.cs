using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using R3;
using ReactiveUI;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Pages;

public partial class ProtocolRegistrationTestPageView : ReactiveUserControl<IProtocolRegistrationTestPageViewModel>
{
    public ProtocolRegistrationTestPageView()
    {
        InitializeComponent();

        this.WhenActivated(disposable =>
        {
            this.BindCommand(ViewModel, vm => vm.CommandStartTest, view => view.ButtonStartTest)
                .AddTo(disposable);

            this.BindCommand(ViewModel, vm => vm.CommandStopTest, view => view.ButtonStopTest)
                .AddTo(disposable);

            this.WhenViewModelValue(vm => vm.WhenAnyValue(
                    x => x.IsTestRunning.Value,
                    x => x.HasTestResult.Value,
                    x => x.FailedTest.Value))
                .OnUI()
                .Subscribe(tuple =>
                {
                    var (isTestRunning, hasTestResult, failedTest) = tuple;
                    WaitingPanel.IsVisible = isTestRunning;
                    ResultPanel.IsVisible = hasTestResult;

                    TextResult.Text = failedTest ? "The test failed" : "The test succeeded";
                }).AddTo(disposable);
        });
    }
}

