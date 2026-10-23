using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SilverModManager.App.UI.Controls.LoadoutBadge;
using ReactiveUI;

namespace SilverModManager.App.UI.Controls.Spine.Buttons.Image;

public class ImageButtonDesignViewModel : ImageButtonViewModel
{
    public ImageButtonDesignViewModel()
    {
        Image = new Bitmap(AssetLoader.Open(new Uri("avares://SilverModManager.App.UI/Assets/DesignTime/cyberpunk_game.png")));
        Click = ReactiveCommand.Create(() => { IsActive = !IsActive; });
        Name = "Image Text";
        LoadoutBadgeViewModel = new LoadoutBadgeDesignViewModel();
    }
}
