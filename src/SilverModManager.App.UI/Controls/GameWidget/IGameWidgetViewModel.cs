using System.Reactive;
using Avalonia.Media.Imaging;
using SilverModManager.Sdk.Games;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Icons;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.GameWidget;

public interface IGameWidgetViewModel : IViewModelInterface
{
    public GameInstallation? Installation { get; set; }
    public string Name { get; }
    public string Version { get; }
    public string Store { get; }
    public IconValue GameStoreIcon { get; }
    public Bitmap Image { get; }
    public ReactiveCommand<Unit, Unit> AddGameCommand { get; set; }
    public ReactiveCommand<Unit, Unit> ViewGameCommand { get; set; }
    public ReactiveCommand<Unit, Unit> RemoveAllLoadoutsCommand { get; set; }

    /// <summary>Moves this tile one place earlier on My Games; the keyboard path for drag and drop.</summary>
    public ReactiveCommand<Unit, Unit>? MoveEarlier { get; set; }

    /// <summary>Moves this tile one place later on My Games.</summary>
    public ReactiveCommand<Unit, Unit>? MoveLater { get; set; }

    public IObservable<bool> IsManagedObservable { get; set; }
    public GameWidgetState State { get; set; }
}
