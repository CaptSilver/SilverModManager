using SilverModManager.App.UI.Overlays;

namespace SilverModManager.App.UI.Input.Menu;

public sealed class AppMenuOverlayViewModel(IReadOnlyList<AppMenuItem> items)
    : AOverlayViewModel<IAppMenuOverlayViewModel>, IAppMenuOverlayViewModel
{
    public IReadOnlyList<AppMenuItem> Items => items;

    public void Choose(AppMenuItem item)
    {
        if (!item.IsEnabled) return;

        // Closed first: an entry that opens a page or starts a launch should not leave the menu on top of it.
        Close();
        item.Run();
    }
}
