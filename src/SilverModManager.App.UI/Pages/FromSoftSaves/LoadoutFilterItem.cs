using SilverModManager.App.UI.Resources;

namespace SilverModManager.App.UI.Pages.FromSoftSaves;

/// <summary>
/// One choice of the saves page's loadout filter.
/// </summary>
/// <param name="Slot">
/// The loadout's slot id (see <c>LoadoutSaveSlot</c>), <see cref="NoLoadout"/> for the saves no loadout owns, or
/// <c>null</c> for every save.
/// </param>
/// <param name="Name">What the list shows.</param>
public sealed record LoadoutFilterItem(string? Slot, string Name)
{
    /// <summary>The <see cref="Slot"/> of the choice that shows only the saves no loadout owns. No slot id can be empty.</summary>
    public const string NoLoadout = "";

    public static readonly LoadoutFilterItem All = new(null, Language.FromSoftSaves_AllSaves);
}
