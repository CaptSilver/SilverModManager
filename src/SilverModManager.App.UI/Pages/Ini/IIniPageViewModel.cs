using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Ini;

/// <summary>
/// The INI page of a loadout: one tab per INI file the game reads, each an <see cref="IniFileEditor"/>.
/// </summary>
public interface IIniPageViewModel : IPageViewModelInterface
{
    LoadoutId LoadoutId { get; set; }

    IReadOnlyList<IniFileEditor> Files { get; }

    IniFileEditor? SelectedFile { get; set; }
}
