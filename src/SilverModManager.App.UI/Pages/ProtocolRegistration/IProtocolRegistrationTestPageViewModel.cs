using SilverModManager.App.UI.WorkspaceSystem;
using R3;

namespace SilverModManager.App.UI.Pages;

public interface IProtocolRegistrationTestPageViewModel : IPageViewModelInterface
{
    ReactiveCommand CommandStartTest { get; }
    ReactiveCommand CommandStopTest { get; }
    IReadOnlyBindableReactiveProperty<bool> IsTestRunning { get; }
    IReadOnlyBindableReactiveProperty<bool> HasTestResult { get; }
    IReadOnlyBindableReactiveProperty<bool> FailedTest { get; }
}
