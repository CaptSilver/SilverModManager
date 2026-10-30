using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// The one line a sync logs about what it did to the game folder. A sync that only deletes otherwise leaves
/// "Extracting 0 files" and nothing else, which reads as a no-op.
/// </summary>
internal static class SyncSummary
{
    private const int FoldersListed = 3;
    private const int FolderDepth = 2;

    /// <param name="subject">What was synced, as the start of the line: "Sync of loadout X".</param>
    /// <param name="groups">The tree split by the backend that carries each part out.</param>
    /// <param name="foldersRemoved">Empty folders the backends removed after the commit.</param>
    public static string Describe(string subject, IReadOnlyDictionary<GamePath, SyncNode> tree, IEnumerable<(DeploymentBackendId Backend, IReadOnlyDictionary<GamePath, SyncNode> Nodes)> groups, int foldersRemoved)
    {
        var deployed = groups
            .Select(g => (g.Backend, Count: g.Nodes.Count(static n => n.Value.Actions.HasFlag(Actions.ExtractToDisk))))
            .Where(static g => g.Count > 0)
            .ToArray();

        var deleted = new List<GamePath>();
        var replaced = 0;
        var backedUp = 0;
        var ingested = 0;
        foreach (var (path, node) in tree)
        {
            if (node.Actions.HasFlag(Actions.DeleteFromDisk))
            {
                if (node.Actions.HasFlag(Actions.ExtractToDisk)) replaced++;
                else deleted.Add(path);
            }
            if (node.Actions.HasFlag(Actions.BackupFile)) backedUp++;
            if (node.Actions.HasFlag(Actions.IngestFromDisk)) ingested++;
        }

        var deployedTotal = deployed.Sum(static g => g.Count);
        var line = $"{subject}: deployed {Files(deployedTotal)}";
        if (deployed.Length > 0) line += " (" + string.Join(", ", deployed.Select(static g => $"{g.Backend.Value} {g.Count}")) + ")";
        line += $", deleted {Files(deleted.Count)}, replaced {replaced}, backed up {backedUp}, ingested {ingested}, removed {foldersRemoved} empty {(foldersRemoved == 1 ? "folder" : "folders")}";

        if (deleted.Count > 0) line += ". Most deletions in " + string.Join(", ", TopFolders(deleted));
        return line;
    }

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    private static IEnumerable<string> TopFolders(List<GamePath> deleted)
        => deleted
            .GroupBy(FolderOf)
            .OrderByDescending(static g => g.Count()).ThenBy(static g => g.Key, StringComparer.Ordinal)
            .Take(FoldersListed)
            .Select(static g => $"{g.Key} ({g.Count()})");

    /// <summary>The folder a deleted file sat in, cut to its first two levels so a mod's files add up to one entry.</summary>
    private static string FolderOf(GamePath path)
    {
        var parts = path.Parent.Parts.Take(FolderDepth).Select(static p => p.ToString());
        return "{" + path.LocationId + "}/" + string.Join('/', parts);
    }
}
