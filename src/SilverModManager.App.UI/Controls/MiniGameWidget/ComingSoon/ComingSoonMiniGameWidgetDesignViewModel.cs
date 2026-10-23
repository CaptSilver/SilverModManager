using SilverModManager.App.UI.Controls.MiniGameWidget.Standard;
using ReactiveUI;
using System.Reactive;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Controls.MiniGameWidget.ComingSoon;

public class ComingSoonMiniGameWidgetViewModelDesignViewModel : AViewModel<IComingSoonMiniGameWidgetViewModel>, IComingSoonMiniGameWidgetViewModel
{
    public static MiniGameWidgetDesignViewModel Instance { get; } = new();

    public ReactiveCommand<Unit, Unit> ViewRoadmapCommand { get; } = ReactiveCommand.CreateFromTask(() => Task.CompletedTask);
}
