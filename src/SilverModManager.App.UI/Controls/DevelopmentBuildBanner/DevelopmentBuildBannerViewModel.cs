using System.Reactive;
using NexusMods.Sdk;
using NexusMods.UI.Sdk;
using ReactiveUI;

namespace NexusMods.App.UI.Controls.DevelopmentBuildBanner;

public class DevelopmentBuildBannerViewModel : AViewModel<IDevelopmentBuildBannerViewModel>, IDevelopmentBuildBannerViewModel
{
    private static readonly Uri GiveFeedbackUri = new("https://github.com/CaptSilver/SilverModManager/issues");

    public ReactiveCommand<Unit, Unit> GiveFeedbackCommand { get; }
    public DevelopmentBuildBannerViewModel(IOSInterop osInterop)
    {
        GiveFeedbackCommand = ReactiveCommand.Create(() => osInterop.OpenUri(GiveFeedbackUri));
    }
}
