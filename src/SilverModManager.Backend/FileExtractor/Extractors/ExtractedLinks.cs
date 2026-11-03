using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Backend.FileExtractor.Extractors;

/// <summary>
/// Removes the symbolic links an archive can leave in an extraction folder. A link that resolves outside
/// the folder would let a later read import, or a later write overwrite, a file the archive never
/// contained (<c>link -> ~/.ssh/id_ed25519</c>), so none survive. Links that stay inside the folder are
/// replaced by a copy of the file they point at, because the file store and the deployment backends
/// assume regular files; links to folders, dangling links and link loops are dropped.
/// </summary>
internal static class ExtractedLinks
{
    internal static void Sanitize(string root, string archive, ILogger logger)
    {
        if (!Directory.Exists(root)) return;

        var links = FindLinks(root);
        if (links.Count == 0) return;

        var lexicalRoot = TrimSeparator(Path.GetFullPath(root));
        var realRoot = LinkResolution.RealPath(lexicalRoot) ?? throw new InvalidOperationException($"Extraction folder `{root}` is part of a link loop");

        // Every link is judged against the tree as extracted, before any is removed: dropping a folder
        // link first would change what the links that go through it resolve to.
        var decisions = links.Select(link => (Link: link, Decision: Judge(link, new FileInfo(link).LinkTarget ?? throw new InvalidOperationException($"`{link}` is not a symbolic link"), lexicalRoot, realRoot))).ToArray();
        foreach (var (link, decision) in decisions)
        {
            var entry = Path.GetRelativePath(root, link);
            File.Delete(link);

            if (decision.CopyFrom is { } source)
            {
                logger.LogWarning("Replacing symlink `{Entry}` in archive `{Archive}` with a copy of `{Target}`", entry, archive, Path.GetRelativePath(root, source));
                File.Copy(source, link);
            }
            else
            {
                logger.LogWarning("Dropping symlink `{Entry}` from archive `{Archive}`: {Reason}", entry, archive, decision.Reason);
            }
        }
    }

    internal readonly record struct Decision(string? CopyFrom, string? Reason, bool TargetMissing = false);

    /// <summary>
    /// Handles the link entries an extractor read from the archive and did not create: each becomes a copy of
    /// its target when the target is a file inside <paramref name="root"/>, and is dropped with a Warning
    /// naming the entry otherwise. A file or folder already at an entry's path (an entry written through the
    /// link, or an earlier entry with the same cleaned name) is kept and the link dropped. Call it after every
    /// other entry is extracted, so a target that comes later in the archive exists. A link whose target is
    /// another link entry is retried until no further link can be placed, so chains resolve in any order.
    /// </summary>
    internal static void PlaceAll(string root, string archive, IEnumerable<(string Name, string Path, string Target)> links, ILogger logger)
    {
        var pending = links.ToList();
        bool progressed;
        do
        {
            progressed = false;
            foreach (var link in pending.ToArray())
            {
                var outcome = Place(root, archive, link.Name, link.Path, link.Target, logger);
                if (outcome == PlaceOutcome.TargetMissing) continue;

                pending.Remove(link);
                progressed = true;
            }
        }
        while (progressed && pending.Count > 0);

        foreach (var link in pending)
        {
            logger.LogWarning("Dropping symlink `{Entry}` from archive `{Archive}`: `{Target}` is not a file inside the archive", link.Name, archive, link.Target);
        }
    }

    private enum PlaceOutcome { Placed, Dropped, TargetMissing }

    private static PlaceOutcome Place(string root, string archive, string entry, string cleanedPath, string target, ILogger logger)
    {
        var linkPath = Path.GetFullPath(Path.Combine(root, cleanedPath));
        if (File.Exists(linkPath) || Directory.Exists(linkPath))
        {
            logger.LogWarning("Dropping symlink `{Entry}` from archive `{Archive}`: another entry already extracted to `{Path}`", entry, archive, cleanedPath);
            return PlaceOutcome.Dropped;
        }

        var realRoot = LinkResolution.RealPath(TrimSeparator(Path.GetFullPath(root)));
        var parent = Path.GetDirectoryName(linkPath) ?? throw new InvalidOperationException($"`{linkPath}` has no parent directory");
        var realParent = LinkResolution.RealPath(parent);
        if (realRoot is null || realParent is null || !IsInside(realParent, realRoot))
        {
            logger.LogWarning("Dropping symlink `{Entry}` from archive `{Archive}`: its folder resolves outside the extraction folder", entry, archive);
            return PlaceOutcome.Dropped;
        }

        var decision = Judge(root, linkPath, target);
        if (decision.TargetMissing) return PlaceOutcome.TargetMissing;
        if (decision.CopyFrom is not { } source)
        {
            logger.LogWarning("Dropping symlink `{Entry}` from archive `{Archive}`: {Reason}", entry, archive, decision.Reason);
            return PlaceOutcome.Dropped;
        }

        logger.LogWarning("Replacing symlink `{Entry}` in archive `{Archive}` with a copy of `{Target}`", entry, archive, Path.GetRelativePath(root, source));
        Directory.CreateDirectory(parent);
        File.Copy(source, linkPath);
        return PlaceOutcome.Placed;
    }

    /// <summary>
    /// Decides what a link at <paramref name="linkPath"/> (which need not exist) pointing at
    /// <paramref name="target"/> becomes. Judged lexically and by real path, so neither a <c>..</c> nor a link
    /// on the way can lead out of <paramref name="root"/>.
    /// </summary>
    internal static Decision Judge(string root, string linkPath, string target)
    {
        var lexicalRoot = TrimSeparator(Path.GetFullPath(root));
        var realRoot = LinkResolution.RealPath(lexicalRoot) ?? throw new InvalidOperationException($"Extraction folder `{root}` is part of a link loop");
        return Judge(linkPath, target, lexicalRoot, realRoot);
    }

    private static Decision Judge(string link, string target, string lexicalRoot, string realRoot)
    {
        var directory = Path.GetDirectoryName(link) ?? throw new InvalidOperationException($"`{link}` has no parent directory");

        if (!IsInside(Path.GetFullPath(target, directory), lexicalRoot)) return new Decision(null, $"it points outside the extraction folder (`{target}`)");

        var real = LinkResolution.RealPath(Path.Combine(directory, target));
        if (real is null) return new Decision(null, $"`{target}` is part of a link loop");
        if (!IsInside(real, realRoot)) return new Decision(null, $"`{target}` resolves outside the extraction folder");

        // Real files only: a folder link would hand the library a tree it never extracted.
        if (File.Exists(real)) return new Decision(real, null);
        return new Decision(null, $"`{target}` is not a file inside the archive", TargetMissing: !Directory.Exists(real));
    }

    private static List<string> FindLinks(string root)
    {
        var links = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var info in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (info.LinkTarget is not null) links.Add(info.FullName);
                else if (info is DirectoryInfo) pending.Push(info.FullName);
            }
        }

        return links;
    }

    private static string TrimSeparator(string path) => path.Length > 1 ? path.TrimEnd('/') : path;

    private static bool IsInside(string path, string root) => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
}
