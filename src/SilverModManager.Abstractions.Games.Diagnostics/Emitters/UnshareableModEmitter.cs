using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Diagnostics.Emitters;

/// <summary>
/// Shows what sharing a user-made collection would leave out: the mods its manifest cannot name and the stored
/// rules that name one of those or a group outside the collection. Recomputed on each Health Check from
/// <see cref="ShareScope"/>, the same verdict the Share dialog writes from.
/// </summary>
public sealed class UnshareableModEmitter : ADroppedItemEmitter
{
    private const string ModReason = "it has no Nexus file or download the collection could name, so sharing leaves it out";
    private const string RuleReason = "sharing the collection leaves this rule out";

    protected override async IAsyncEnumerable<DroppedItem> Dropped(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var db = loadout.Db;
        var rules = LoadoutGroupRule.FindByLoadout(db, loadout.LoadoutId).ToArray();
        foreach (var collection in LoadoutItem.FindByLoadout(db, loadout.LoadoutId).OfTypeLoadoutItemGroup().OfTypeCollectionGroup().OfTypeManagedCollectionLoadoutGroup())
        {
            var group = collection.AsCollectionGroup().AsLoadoutItemGroup();
            var source = $"collection {group.AsLoadoutItem().Name}";
            var scope = ShareScope.Of(group);

            foreach (var skipped in scope.Skipped)
                yield return new DroppedItem(skipped.AsLoadoutItem().Name, source, ModReason, "Library");

            foreach (var rule in rules)
            {
                // A rule between a shared mod and one of the user's own is normal, and no manifest could carry it.
                (string Why, DiagnosticSeverity Severity)? verdict = scope.Classify(rule) switch
                {
                    RuleShare.UnnameableEnd => ("one of its mods cannot be named", DiagnosticSeverity.Warning),
                    RuleShare.OutsideEnd => ("it names a mod outside the collection", DiagnosticSeverity.Suggestion),
                    _ => null,
                };
                if (verdict is not var (why, severity)) continue;
                yield return new DroppedItem(GroupRuleText.WinsOver(rule), source, $"{RuleReason}: {why}", "Load Order", severity);
            }
        }
    }
}
