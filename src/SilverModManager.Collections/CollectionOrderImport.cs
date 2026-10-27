using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Collections;

/// <summary>Applies a collection's order once its mods are installed, for the whole-collection install and the one-download install alike.</summary>
internal static class CollectionOrderImport
{
    /// <summary>
    /// Reads the order the manifest carries and the rules it could not resolve, and imports both into <paramref name="tx"/>.
    /// The transaction must not already write the loadout's priority rows: the import's SetGroupOrder has to be the only writer.
    /// </summary>
    /// <param name="newGroups">The groups this run installed into a collection that already had some; null when every group is new.</param>
    public static void Import(
        IServiceProvider provider, IDb db, LoadoutId loadoutId, IGame game, CollectionRoot root,
        CollectionRevisionMetadata.ReadOnly revision, LoadoutItemGroupId container,
        IReadOnlySet<LoadoutItemGroupId>? newGroups, ITransaction tx)
    {
        var order = CollectionOrderReader.Read(db, game, root, container);

        // The importer replaces the container's rows with one list, so the rule drops ride along (and run even when the collection has no order key).
        var ruleDrops = CollectionRuleDrops.Read(db, root, revision);
        order = ModOrder.Create(order.WinnerToLoser, order.Dropped.Concat(ruleDrops));
        provider.GetRequiredService<ModOrderImporter>().Import(
            Loadout.Load(db, loadoutId), order,
            new ModOrderSource($"collection {revision.Collection.Name}", container, root, NewGroups: newGroups), tx);
    }

    /// <summary>The direct child groups of <paramref name="container"/>.</summary>
    public static HashSet<LoadoutItemGroupId> Children(IDb db, LoadoutItemGroupId container) =>
        LoadoutItemGroup.Load(db, container).Children.OfTypeLoadoutItemGroup().Select(static child => child.LoadoutItemGroupId).ToHashSet();
}
