using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SilverModManager.UI.Sdk.Icons;

namespace SilverModManager.App.UI.WorkspaceSystem;

public class PanelTabHeaderDesignViewModel : PanelTabHeaderViewModel
{
    public PanelTabHeaderDesignViewModel() : base(PanelTabId.DefaultValue)
    {
        Title = "Very long name for tab headers";

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://SilverModManager.App.UI/Assets/DesignTime/cyberpunk_game.png"));
            Icon = new AvaloniaImage(new Bitmap(stream));
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }
}
