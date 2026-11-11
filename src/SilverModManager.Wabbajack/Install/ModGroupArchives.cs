using SilverModManager.Sdk.Hashes;
using SilverModManager.Wabbajack.Format;
using SilverModManager.Wabbajack.Mapping;

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

    private static void Count(Dictionary<FileIdentity, int> perArchive, FileIdentity archive)
        => perArchive[archive] = perArchive.GetValueOrDefault(archive) + 1;

    // The same folding the job keys BsaInputs by.
    private static string Fold(string path) => path.Replace('\\', '/').ToLowerInvariant();
}
