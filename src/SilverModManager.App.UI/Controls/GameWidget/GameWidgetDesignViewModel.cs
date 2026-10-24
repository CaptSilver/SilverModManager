using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SilverModManager.Sdk.Games;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.App.UI.Controls.GameWidget;

public partial class GameWidgetDesignViewModel : AViewModel<IGameWidgetViewModel>, IGameWidgetViewModel
{
    [Reactive] public partial GameInstallation? Installation { get; set; } = null!;
    public string Name { get; } = "Cyberpunk 2077";
    public string Version { get; set; }
    public string Store { get; set; }
    public IconValue GameStoreIcon { get; set; }
    public Bitmap Image { get; }
    public ReactiveCommand<Unit,Unit> AddGameCommand { get; set; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> ViewGameCommand { get; set; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> RemoveAllLoadoutsCommand { get; set; } = ReactiveCommand.Create(() => { });
    
    public IObservable<bool> IsManagedObservable { get; set; } = Observable.Return(false);

    [Reactive]
    public partial GameWidgetState State { get; set; }
 
    public GameWidgetDesignViewModel()
    {
        Image = new Bitmap(AssetLoader.Open(new Uri("avares://SilverModManager.App.UI/Assets/DesignTime/tile.webp")));
        State = GameWidgetState.ManagedGame;
        Version = $"Version: 1.5.6";
        Store = "Foo";
        GameStoreIcon = GameWidgetViewModel.MapGameStoreToIcon(GameStore.EGS);
    }
}
