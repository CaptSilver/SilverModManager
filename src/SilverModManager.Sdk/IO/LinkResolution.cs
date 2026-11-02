namespace SilverModManager.Sdk.IO;

/// <summary>Resolves symbolic links in paths that may not exist yet.</summary>
public static class LinkResolution
{
    private const int MaxLinkExpansions = 40;

    /// <summary>
    /// <c>realpath</c> without the file needing to exist: resolves every link on the way, and a <c>..</c>
    /// following a link steps out of the link's target, not out of the link's own folder. Returns null
    /// for a link loop. <paramref name="path"/> must be absolute.
    /// </summary>
    public static string? RealPath(string path)
    {
        var pending = new LinkedList<string>(path.Split('/'));
        var current = "/";
        var expansions = 0;

        while (pending.First is { } node)
        {
            pending.RemoveFirst();
            var part = node.Value;
            if (part.Length == 0 || part == ".") continue;
            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? "/";
                continue;
            }

            var next = Path.Combine(current, part);
            var target = new FileInfo(next).LinkTarget;
            if (target is null)
            {
                current = next;
                continue;
            }

            if (++expansions > MaxLinkExpansions) return null;
            if (Path.IsPathRooted(target)) current = "/";

            var parts = target.Split('/');
            for (var i = parts.Length - 1; i >= 0; i--) pending.AddFirst(parts[i]);
        }

        return current;
    }
}
