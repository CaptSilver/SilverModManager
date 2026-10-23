using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;

public partial class LoadoutItemGroupPriority : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.LoadoutItemGroupPriority";

    public static readonly ReferenceAttribute<Loadout> Loadout = new(Namespace, nameof(Loadout));
    public static readonly ReferenceAttribute<LoadoutItemGroup> Target = new(Namespace, nameof(Target));
    public static readonly ConflictPriorityAttribute Priority = new(Namespace, nameof(Priority));
}
