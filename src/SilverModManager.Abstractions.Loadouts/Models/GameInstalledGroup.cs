using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// Everything the game installed into its own folder (Creations, in-game mods) that the app did not put
/// there. One per loadout, created when the first such item is recognised. Switching the group off
/// (<see cref="LoadoutItem.Disabled"/>) switches all of its items off at once.
/// </summary>
[Include<LoadoutItemGroup>]
public partial class GameInstalledGroup : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.GameInstalledGroup";

    public static readonly ReferenceAttribute<Loadout> InstalledFor = new(Namespace, nameof(InstalledFor)) { IsIndexed = true };
}
