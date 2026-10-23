using System.Reactive;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.DevelopmentBuildBanner;

public class DevelopmentBuildBannerDesignViewModel : AViewModel<IDevelopmentBuildBannerViewModel>, IDevelopmentBuildBannerViewModel
{
    public ReactiveCommand<Unit, Unit> GiveFeedbackCommand { get; } = ReactiveCommand.Create(() => { });

    public DevelopmentBuildBannerDesignViewModel() { }
}
