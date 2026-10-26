using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;

/// <summary>
/// An ordering constraint between two groups of one loadout. The rules are inputs to the priority resolver;
/// the priority rows stay the only order the deployment reads.
/// </summary>
public partial class LoadoutGroupRule : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.LoadoutGroupRule";

    /// <summary>The loadout that owns the rule.</summary>
    public static readonly ReferenceAttribute<Loadout> Loadout = new(Namespace, nameof(Loadout)) { IsIndexed = true };

    /// <summary>The group the rule is about.</summary>
    public static readonly ReferenceAttribute<LoadoutItemGroup> Source = new(Namespace, nameof(Source)) { IsIndexed = true };

    /// <summary>The group <see cref="Source"/> is ordered against.</summary>
    public static readonly ReferenceAttribute<LoadoutItemGroup> Other = new(Namespace, nameof(Other));

    /// <summary>How <see cref="Source"/> is ordered against <see cref="Other"/>.</summary>
    public static readonly EnumByteAttribute<GroupRuleKind> Kind = new(Namespace, nameof(Kind));
}

/// <summary>
/// What a <see cref="LoadoutGroupRule"/> demands of the priority order.
/// </summary>
public enum GroupRuleKind : byte
{
    /// <summary>The source group wins over the other group.</summary>
    After = 0,

    /// <summary>The source group loses to the other group.</summary>
    Before = 1,
}
