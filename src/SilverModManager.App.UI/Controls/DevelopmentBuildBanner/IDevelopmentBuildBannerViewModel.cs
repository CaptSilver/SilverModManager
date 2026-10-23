using System.Reactive;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.DevelopmentBuildBanner;

public interface IDevelopmentBuildBannerViewModel : IViewModelInterface
{
    public ReactiveCommand<Unit, Unit> GiveFeedbackCommand { get; }
}
