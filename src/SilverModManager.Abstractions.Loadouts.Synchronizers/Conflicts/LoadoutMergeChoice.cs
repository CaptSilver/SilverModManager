using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;

/// <summary>
/// Pins which mod's edit a merged file keeps for one contested entry (a param row, a text, a texture), whatever
/// the group priorities say. Lives here, not with the game that reads it, because the loadout's tracked-entities
/// query names it and every host that applies a loadout has to define it.
/// </summary>
public partial class LoadoutMergeChoice : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.LoadoutMergeChoice";

    /// <summary>The loadout that owns the choice.</summary>
    public static readonly ReferenceAttribute<Loadout> Loadout = new(Namespace, nameof(Loadout)) { IsIndexed = true };

    /// <summary>The merged file the entry belongs to, package-relative as the merge report spells it.</summary>
    public static readonly StringAttribute Path = new(Namespace, nameof(Path));

    /// <summary>Names the entry: <c>param:&lt;entry&gt;/&lt;row&gt;</c>, <c>fmg:&lt;entry&gt;/&lt;text&gt;</c> or <c>tpf:&lt;name&gt;</c>.</summary>
    public static readonly StringAttribute Key = new(Namespace, nameof(Key));

    /// <summary>The group whose edit stays.</summary>
    public static readonly ReferenceAttribute<LoadoutItemGroup> WinnerGroup = new(Namespace, nameof(WinnerGroup));

    /// <summary>
    /// The Nexus mod id of <see cref="WinnerGroup"/> when it has one. Read first, so the choice survives the
    /// mod being reinstalled as a new group; the group is the fallback for local mods.
    /// </summary>
    public static readonly UInt32Attribute WinnerModId = new(Namespace, nameof(WinnerModId)) { IsOptional = true };
}
