using NexusMods.Paths;

namespace SilverModManager.Games.FromSoft.Cleanup;

/// <summary>Lists the game folder before and after a cleanup and says what differs.</summary>
public static class GameFolderInventory
{
    /// <summary>
    /// Every entry below <paramref name="gameFolder"/>, sorted by path ignoring case. A link is listed as one entry
    /// and never entered, so a link to a folder somewhere else does not pull that folder into the list.
    /// </summary>
    public static IReadOnlyList<InventoryEntry> Take(AbsolutePath gameFolder)
    {
        var entries = new List<InventoryEntry>();
        Walk(new DirectoryInfo(gameFolder.GetFullPath()), "", entries);
        return entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Path, StringComparer.Ordinal).ToArray();
    }

    private static void Walk(DirectoryInfo directory, string prefix, List<InventoryEntry> entries)
    {
        foreach (var info in directory.EnumerateFileSystemInfos())
        {
            var path = prefix + info.Name;
            if (info.LinkTarget is { } target) entries.Add(new InventoryEntry(path, InventoryKind.Link, 0, target));
            else if (info is DirectoryInfo subfolder)
            {
                entries.Add(new InventoryEntry(path, InventoryKind.Directory, 0));
                Walk(subfolder, path + "/", entries);
            }
            else entries.Add(new InventoryEntry(path, InventoryKind.File, ((FileInfo)info).Length));
        }
    }

    /// <summary>One line per entry: what it is, its path and its size or where it points.</summary>
    public static IReadOnlyList<string> Describe(IReadOnlyList<InventoryEntry> inventory)
        => inventory.Select(e => e.Kind switch
        {
            InventoryKind.Link => $"link  {e.Path} -> {e.LinkTarget}",
            InventoryKind.Directory => $"dir   {e.Path}/",
            _ => $"file  {e.Path} ({e.Size} bytes)",
        }).ToArray();

    /// <summary>The entries that were removed, added or changed between the two inventories.</summary>
    public static IReadOnlyList<string> Diff(IReadOnlyList<InventoryEntry> before, IReadOnlyList<InventoryEntry> after)
    {
        var was = before.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var now = after.ToDictionary(e => e.Path, StringComparer.Ordinal);

        return was.Keys.Union(now.Keys)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => (path, was.GetValueOrDefault(path), now.GetValueOrDefault(path)))
            .Select(t => t switch
            {
                (var path, not null, null) => $"removed  {path}",
                (var path, null, not null) => $"added    {path}",
                (var path, { } old, { } current) when old != current => $"changed  {path}",
                _ => null,
            })
            .OfType<string>()
            .ToArray();
    }
}
