using SilverModManager.App.UI.Overlays;

namespace SilverModManager.App.UI.Input.Menu;

/// <summary>The menu the pad's Menu button opens: the few things worth reaching without a pointer from any page.</summary>
public interface IAppMenuOverlayViewModel : IOverlayViewModel
{
    IReadOnlyList<AppMenuItem> Items { get; }

    /// <summary>Runs the entry and closes the menu. Does nothing for an entry that cannot run.</summary>
    void Choose(AppMenuItem item);
}
