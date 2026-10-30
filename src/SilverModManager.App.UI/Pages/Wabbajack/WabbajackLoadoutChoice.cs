using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.App.UI.Pages.Wabbajack;

/// <summary>An entry of the target loadout picker. A null <paramref name="Id"/> is the "Create new loadout" entry.</summary>
public sealed record WabbajackLoadoutChoice(LoadoutId? Id, string Name)
{
    public static readonly WabbajackLoadoutChoice CreateNew = new(null, "Create new loadout");

    // The picker has no item template, so this is the text it shows.
    public override string ToString() => Name;
}
