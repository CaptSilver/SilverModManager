using System.Reactive;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;
using SilverModManager.UI.Sdk;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Controls.MiniGameWidget.Standard;

public partial class MiniGameWidgetDesignViewModel : AViewModel<IMiniGameWidgetViewModel>, IMiniGameWidgetViewModel
{
    public static MiniGameWidgetDesignViewModel Instance { get; } = new();

    [Reactive] public partial IGame? Game { get; set; }
    public GameInstallation[]? GameInstallations { get; set; }
    public string Name { get; set; } = "Cyberpunk 2077";
    public bool IsFound { get; set; } = true;
    public Bitmap Image { get; set; } = new(AssetLoader.Open(new Uri("avares://SilverModManager.App.UI/Assets/DesignTime/cyberpunk_game.png")));

    public ReactiveCommand<Unit, Unit> GiveFeedbackCommand { get; } = ReactiveCommand.CreateFromTask(() => Task.CompletedTask);
}
