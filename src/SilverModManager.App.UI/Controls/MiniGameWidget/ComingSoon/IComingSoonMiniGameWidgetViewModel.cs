using ReactiveUI;
using System.Reactive;
using SilverModManager.UI.Sdk;

namespace SilverModManager.App.UI.Controls.MiniGameWidget.ComingSoon;

public interface IComingSoonMiniGameWidgetViewModel : IViewModelInterface
{
    ReactiveCommand<Unit, Unit> ViewRoadmapCommand { get; }
}
