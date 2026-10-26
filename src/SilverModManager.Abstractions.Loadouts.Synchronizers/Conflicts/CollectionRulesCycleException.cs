using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;

/// <summary>
/// Thrown when a collection's ordering rules can't all hold, because they contradict each other or a rule already
/// on the loadout. None of the collection's rules are written and the load order stays as it was.
/// </summary>
[PublicAPI]
public class CollectionRulesCycleException(string collectionName, string firstGroupName, string secondGroupName)
    : UserFacingException(
        $"The load order rules of the collection \"{collectionName}\" can't all hold: they put \"{firstGroupName}\" and \"{secondGroupName}\" " +
        "both before and after each other, directly or together with a rule already on the loadout. " +
        "Its mods were installed, but none of its rules were added and the install stopped before enabling the collection. " +
        "If a rule you added contradicts the collection, remove it and install the collection again."
    )
{
    /// <inheritdoc />
    public override string Title => "The collection's load order was not applied";
}
