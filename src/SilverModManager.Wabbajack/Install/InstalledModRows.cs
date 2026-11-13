using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Loadouts.Install;
using SilverModManager.Wabbajack.Mapping;
using SilverModManager.Wabbajack.Models;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// The per-mod rows of an install that is over, read from what it stored. The live rows belong to the job and are gone
/// with it; a page that opens later, or after the run, shows these instead.
/// </summary>
public static class InstalledModRows
{
    public static ModInstallRows Read(IDb db, WabbajackModlistLoadoutGroupId group)
    {
        var mods = LoadoutItem.FindByParent(db, group.Value).OfTypeLoadoutItemGroup()
            .Select(child => WabbajackModGroup.Load(db, child.Id))
            .Where(mod => mod.IsValid())
            .ToArray();

        // A folder fails with the first skip that kept something out of it; by-design skips lose nothing.
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unmatched = 0;
        var known = mods.Select(mod => mod.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var skipped in WabbajackSkippedFile.FindByGroup(db, group.Value).Where(s => !s.Kind.IsByDesign()))
        {
            var folder = Mo2Layout.ModFolderOf(NexusMods.Paths.RelativePath.FromUnsanitizedInput(skipped.To));
            if (folder is not null && known.Contains(folder)) failures.TryAdd(folder, skipped.Reason);
            else unmatched++;
        }

        var rows = new ModInstallRows();
        var built = new List<ModInstallRow>(mods.Length);
        for (var index = 0; index < mods.Length; index++)
        {
            var mod = mods[index];
            var files = LoadoutItem.FindByParent(db, mod.Id).Count();
            var state = failures.TryGetValue(mod.Folder, out var reason)
                ? new ModInstallRowState(ModInstallState.Failed, files, files, files, reason)
                : new ModInstallRowState(ModInstallState.Installed, files, files, files);
            built.Add(new ModInstallRow(mod.Folder, mod.AsLoadoutItemGroup().AsLoadoutItem().Name, index, state));
        }

        rows.Add(built);
        rows.SetTotals(new ModInstallTotals(
            built.Count,
            built.Count(row => row.Current.State == ModInstallState.Installed),
            failures.Count + unmatched));
        return rows;
    }

    /// <summary>
    /// Whether two reads show the same thing. The page reads on every database revision while no run is followed, and
    /// swapping the list for an equal one would throw away the user's place in it.
    /// </summary>
    public static bool Same(ModInstallRows a, ModInstallRows b)
    {
        if (a.CurrentTotals != b.CurrentTotals || a.Count != b.Count) return false;
        foreach (var row in a.Snapshot())
        {
            if (!b.TryGet(row.Key, out var other) || other.Name != row.Name || other.Index != row.Index || other.Current != row.Current) return false;
        }
        return true;
    }
}
