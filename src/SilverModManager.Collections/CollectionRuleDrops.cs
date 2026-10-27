using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Collections;

/// <summary>
/// The collection's before/after <c>modRules</c> that name a mod the collection does not have. They only change with a new
/// revision, so they are stored with the collection's order drops (the importer's replace of the container's rows keeps them).
/// A rule whose mod was merely not installed is <see cref="UnusedCollectionRuleEmitter"/>'s: that changes with every install.
/// </summary>
public static class CollectionRuleDrops
{
    private const string UnresolvedReason = "it names a mod that is not in the collection";

    /// <summary>
    /// A rule the revision stored no <see cref="CollectionDownloadRules"/> row for (the resolver skips a rule when one end
    /// matches no download). Other rule types are not about order and are left out.
    /// </summary>
    public static IReadOnlyList<ModOrderDrop> Read(IDb db, CollectionRoot root, CollectionRevisionMetadata.ReadOnly revision)
    {
        var drops = new List<ModOrderDrop>();
        for (var i = 0; i < root.ModRules.Length; i++)
        {
            var rule = root.ModRules[i];
            if (rule.Type is not (VortexModRuleType.Before or VortexModRuleType.After)) continue;

            // The array index is shared by every revision of a collection, so keep only this revision's row.
            var stored = CollectionDownloadRules.FindByArrayIndex(db, i)
                .Any(row => CollectionDownload.Load(db, row.SourceId).CollectionRevisionId == revision.CollectionRevisionMetadataId);

            var item = $"rule '{Describe(rule.Source)} {(rule.Type == VortexModRuleType.Before ? "before" : "after")} {Describe(rule.Other)}'";
            if (!stored) drops.Add(new ModOrderDrop(item, UnresolvedReason));
        }
        return drops;
    }

    /// <summary>Same fields the resolver matches on, most readable first.</summary>
    private static string Describe(VortexModReference reference)
    {
        if (!string.IsNullOrWhiteSpace(reference.LogicalFileName)) return reference.LogicalFileName;
        if (!string.IsNullOrWhiteSpace(reference.FileExpression)) return reference.FileExpression;
        if (!string.IsNullOrWhiteSpace(reference.Tag)) return reference.Tag;
        return reference.FileMD5 != default ? reference.FileMD5.ToString() : "an unnamed mod";
    }
}
