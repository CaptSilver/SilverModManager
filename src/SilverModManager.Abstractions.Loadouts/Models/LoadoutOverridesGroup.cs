using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// Files found in the game folder that are not part of the loadout are first placed in this group,
/// then later can be moved to other parts of the loadout.
/// </summary>
[Include<LoadoutItemGroup>]
public partial class LoadoutOverridesGroup : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.LoadoutOverridesGroup";

    public static readonly ReferenceAttribute<Loadout> OverridesFor = new(Namespace, nameof(OverridesFor));
}
