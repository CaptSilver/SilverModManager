using System.Reactive;
using Avalonia.Media.Imaging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;
using SilverModManager.UI.Sdk;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.MiniGameWidget.Standard;

public interface IMiniGameWidgetViewModel : IViewModelInterface
{
    public IGame? Game { get; set; }
    public GameInstallation[]? GameInstallations { get; set; }
    public string Name { get; set; }
    public bool IsFound { get; set; }
    public Bitmap Image { get; }
    public ReactiveCommand<Unit, Unit> GiveFeedbackCommand { get; }
}
