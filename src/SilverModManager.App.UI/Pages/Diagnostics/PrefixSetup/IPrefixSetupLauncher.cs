using SilverModManager.Platform.Proton.PrefixSetup;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Diagnostics.PrefixSetup;

/// <summary>
/// Opens the prefix setup for a loadout's game: a dry run, then the preview where the user confirms the prefix and
/// starts the real run. The diagnostic's Set up button and the Tools page's Install button both go through it.
/// </summary>
public interface IPrefixSetupLauncher
{
    /// <summary>Completes when the user closes the preview.</summary>
    Task ShowAsync(LoadoutId loadoutId, IReadOnlyList<PrefixComponentId> components);
}
