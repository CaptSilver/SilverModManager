using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Games;

namespace SilverModManager.App.UI.LeftMenu.Loadout;

/// <summary>
/// What the Library badge in a loadout's left menu counts: items acquired since the loadout's Library page was last
/// open in this process, or since its menu was first built. Nothing is persisted, so a restart shows nothing new.
/// </summary>
public interface ILibraryNewItems
{
    /// <summary>The Library page of the loadout was open; everything acquired up to now is seen.</summary>
    void MarkSeen(LoadoutId loadout);

    /// <summary>
    /// Mods-tab rows for the game created after the loadout's Library page was last opened (or its menu built).
    /// Emits on the connection's event thread; bind it with <c>OnUI</c>.
    /// </summary>
    IObservable<int> ObserveNewCount(LoadoutId loadout, GameId game);
}
