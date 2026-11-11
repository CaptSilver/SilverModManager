using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Library;
using SilverModManager.Wabbajack.Mapping;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// Works out which archive a <c>mods/&lt;Folder&gt;</c> group was mostly built from, and keeps the group's library link
/// pointing at the library file that holds it.
/// </summary>
internal static class ModGroupArchives
{
    /// <summary>Main archive per mods/ folder, by the count-then-list-order rule. Pure; tested on placements alone.</summary>
    public static IReadOnlyDictionary<string, FileIdentity> MainArchiveByFolder(
        IReadOnlyList<InstallModlistJob.Placement> placements,
        IReadOnlyDictionary<string, Dictionary<string, InstallModlistJob.Placement>> bsaInputs,
        IReadOnlyList<ArchiveEntry> archives)
    {
        var counts = new Dictionary<string, Dictionary<FileIdentity, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var placement in placements)
        {
            // Overwrite and the game-root folders are not mods; the folder is the one the group is made from.
            if (placement.Target is not Mo2Target.ModFile { GroupName: var folder } || folder == Mo2Layout.OverwriteGroup) continue;

            if (!counts.TryGetValue(folder, out var perArchive)) counts[folder] = perArchive = new();
            switch (placement.Directive)
            {
                case FromArchive from:
                    Count(perArchive, from.Source.ArchiveXxHash64);
                    break;
                case CreateBsa bsa when bsaInputs.TryGetValue(Fold(bsa.TempId.ToString()), out var inputs):
                    foreach (var input in inputs.Values)
                        if (input.Directive is FromArchive inputFrom) Count(perArchive, inputFrom.Source.ArchiveXxHash64);
                    break;
            }
        }

        var order = new Dictionary<FileIdentity, int>();
        for (var i = 0; i < archives.Count; i++) order.TryAdd(archives[i].XxHash64, i);

        var result = new Dictionary<string, FileIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folder, perArchive) in counts)
        {
            if (perArchive.Count == 0) continue;
            result[folder] = perArchive
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => order.GetValueOrDefault(pair.Key, int.MaxValue))
                .First().Key;
        }
        return result;
    }

    /// <summary>
    /// Writes <see cref="WabbajackModGroup.MainArchive"/> on each folder's group (retracting it where the folder has none)
    /// and writes nothing for a group that already holds the right value, so a rerun over an unchanged list adds no
    /// datoms. Skips the settings group and any group that is not a mod group, such as one an update just deleted.
    /// </summary>
    public static void Record(ITransaction tx, IDb db, IReadOnlyDictionary<string, LoadoutItemGroupId> groups, IReadOnlyDictionary<string, FileIdentity> mainArchives)
    {
        foreach (var (folder, groupId) in groups)
        {
            if (folder == Mo2Layout.ProfileSettingsGroup) continue;
            var group = WabbajackModGroup.Load(db, groupId.Value);
            if (!group.IsValid()) continue;

            var hasCurrent = WabbajackModGroup.MainArchive.TryGetValue(group, out var current);
            if (mainArchives.TryGetValue(folder, out var main))
            {
                if (!hasCurrent || !current.Equals(main)) tx.Add(groupId.Value, WabbajackModGroup.MainArchive, main);
            }
            else if (hasCurrent)
            {
                tx.RetractCurrent(groupId.Value, WabbajackModGroup.MainArchive);
            }
        }
    }

    /// <summary>
    /// Sets <see cref="LibraryLinkedLoadoutItem.LibraryItem"/> on every <see cref="WabbajackModGroup"/> under the modlist
    /// group to the top-level library file holding its main archive, retracts it when no file does, and writes nothing
    /// for a group whose link is already right. "Already right" means the linked entity loads as a library file, is not
    /// an archive member and has the main archive's xxHash64, so two library copies of one archive never flip the link.
    /// A group with no main archive and no link is left alone. Returns the number of groups whose link changed.
    /// </summary>
    /// <param name="fresh">
    /// The main archives <see cref="Record"/> put in the same transaction, which the transaction cannot read back: when
    /// given, they are the truth for every group (a folder missing from it has none), otherwise the database is.
    /// </param>
    public static int Relink(ITransaction tx, IDb db, ILibraryArchiveLocator locator, EntityId modlistGroup, IReadOnlyDictionary<string, FileIdentity>? fresh = null)
    {
        var changed = 0;
        foreach (var child in LoadoutItem.FindByParent(db, modlistGroup).OfTypeLoadoutItemGroup())
        {
            var group = WabbajackModGroup.Load(db, child.Id);
            if (!group.IsValid()) continue;

            FileIdentity? main;
            if (fresh is null) main = WabbajackModGroup.MainArchive.TryGetValue(group, out var recorded) ? recorded : null;
            else main = fresh.TryGetValue(group.Folder, out var named) ? named : null;

            var hasLink = LibraryLinkedLoadoutItem.LibraryItem.TryGetValue(LibraryLinkedLoadoutItem.Load(db, group.Id), out var linked);
            if (main is not { } archive)
            {
                if (!hasLink) continue;
                tx.RetractCurrent(group.Id, LibraryLinkedLoadoutItem.LibraryItem);
                changed++;
                continue;
            }

            if (hasLink && HoldsArchive(db, linked, archive)) continue;

            if (locator.FindTopLevelByXxHash64(db, archive) is { } file)
            {
                tx.Add(group.Id, LibraryLinkedLoadoutItem.LibraryItem, file.Id);
                changed++;
            }
            else if (hasLink)
            {
                tx.RetractCurrent(group.Id, LibraryLinkedLoadoutItem.LibraryItem);
                changed++;
            }
        }
        return changed;
    }

    private static bool HoldsArchive(IDb db, EntityId candidate, FileIdentity archive)
    {
        var file = LibraryFile.Load(db, candidate);
        return file.IsValid()
               && !LibraryArchiveLocator.IsArchiveMember(db, file.Id)
               && file.XxHash64Identity() is { } held
               && held.Equals(archive);
    }

    private static void Count(Dictionary<FileIdentity, int> perArchive, FileIdentity archive)
        => perArchive[archive] = perArchive.GetValueOrDefault(archive) + 1;

    // The same folding the job keys BsaInputs by.
    private static string Fold(string path) => path.Replace('\\', '/').ToLowerInvariant();
}
