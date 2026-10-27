using DynamicData.Kernel;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Collections;

/// <summary>
/// The collection's before/after <c>modRules</c> that cannot order anything. They are reported with the collection's order
/// drops, so the importer's replace of the container's rows keeps them.
/// </summary>
public static class CollectionRuleDrops
{
    private const string UnresolvedReason = "it names a mod that is not in the collection";
    private const string NotInstalledReason = "one of its mods is not installed";

    /// <summary>
    /// A rule the revision stored no <see cref="CollectionDownloadRules"/> row for (the resolver skips a rule when one end
    /// matches no download), and a stored rule with an end that <paramref name="group"/> holds no install of (an optional
    /// download that was declined). Other rule types are not about order and are left out.
    /// </summary>
    public static IReadOnlyList<ModOrderDrop> Read(IDb db, CollectionRoot root, CollectionRevisionMetadata.ReadOnly revision, Optional<CollectionGroup.ReadOnly> group)
    {
        var drops = new List<ModOrderDrop>();
        for (var i = 0; i < root.ModRules.Length; i++)
        {
            var rule = root.ModRules[i];
            if (rule.Type is not (VortexModRuleType.Before or VortexModRuleType.After)) continue;

            // The array index is shared by every revision of a collection, so keep only this revision's row.
            var stored = CollectionDownloadRules.FindByArrayIndex(db, i)
                .Where(row => CollectionDownload.Load(db, row.SourceId).CollectionRevisionId == revision.CollectionRevisionMetadataId)
                .Cast<CollectionDownloadRules.ReadOnly?>()
                .FirstOrDefault();

            var item = $"rule '{Describe(rule.Source)} {(rule.Type == VortexModRuleType.Before ? "before" : "after")} {Describe(rule.Other)}'";
            if (stored is not { } resolved)
                drops.Add(new ModOrderDrop(item, UnresolvedReason));
            else if (!IsInstalled(resolved.Source, group, db) || !IsInstalled(resolved.Other, group, db))
                drops.Add(new ModOrderDrop(item, NotInstalledReason));
        }
        return drops;
    }

    private static bool IsInstalled(CollectionDownload.ReadOnly download, Optional<CollectionGroup.ReadOnly> group, IDb db) =>
        CollectionDownloader.GetStatus(download, group, db).IsInstalled(out _);

    /// <summary>Same fields the resolver matches on, most readable first.</summary>
    private static string Describe(VortexModReference reference)
    {
        if (!string.IsNullOrWhiteSpace(reference.LogicalFileName)) return reference.LogicalFileName;
        if (!string.IsNullOrWhiteSpace(reference.FileExpression)) return reference.FileExpression;
        if (!string.IsNullOrWhiteSpace(reference.Tag)) return reference.Tag;
        return reference.FileMD5 != default ? reference.FileMD5.ToString() : "an unnamed mod";
    }
}
