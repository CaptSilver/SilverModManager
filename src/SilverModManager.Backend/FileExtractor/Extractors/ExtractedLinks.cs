using System.Text;
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
    /// <param name="root">The extraction folder.</param>
    /// <param name="archive">The archive's name, for the warnings.</param>
    /// <param name="logger">Where the warnings go.</param>
    /// <param name="existing">The links that were in the folder before the archive was extracted (see <see cref="Snapshot"/>). A link
    /// that is still the same one is not the archive's and is left exactly as it is; a link at one of those paths that the run
    /// replaced is the archive's and is judged like any other.</param>
    internal static void Sanitize(string root, string archive, ILogger logger, IReadOnlyDictionary<string, LinkIdentity>? existing = null)
    {
        if (!Directory.Exists(root)) return;

        var found = FindLinks(root).Where(link => existing is null || !existing.TryGetValue(link.Key, out var before) || before != IdentityOf(link)).ToList();
        if (found.Count == 0) return;

        // A link whose path is not valid UTF-8 cannot be handed to the string-based judging below (.NET reads such a
        // name back with replacement characters, so its target cannot be read), and none of the rules has judged it,
        // so it goes. unlink removes the link itself and never follows it.
        foreach (var link in found.Where(link => !link.Nameable))
        {
            RawFolderWalk.Unlink(link.Path);
            logger.LogWarning("Dropping symlink `{Entry}` from archive `{Archive}`: its name is not valid UTF-8, so it cannot be judged", EntryNames.Printable(Path.GetRelativePath(root, Encoding.UTF8.GetString(link.Path[..^1]))), archive);
        }

        var links = found.Where(link => link.Nameable).Select(link => link.Key).ToList();
        if (links.Count == 0) return;

        var lexicalRoot = TrimSeparator(Path.GetFullPath(root));
        var realRoot = LinkResolution.RealPath(lexicalRoot) ?? throw new InvalidOperationException($"Extraction folder `{root}` is part of a link loop");

        // Every link is judged against the tree as extracted, before any is removed: dropping a folder
        // link first would change what the links that go through it resolve to.
        var decisions = links.Select(link => (Link: link, Decision: Judge(link, new FileInfo(link).LinkTarget ?? throw new InvalidOperationException($"`{link}` is not a symbolic link"), lexicalRoot, realRoot))).ToArray();
        foreach (var (link, decision) in decisions)
        {
            var entry = Path.GetRelativePath(root, link);

            if (decision.CopyFrom is { } source)
            {
                logger.LogWarning("Replacing symlink `{Entry}` in archive `{Archive}` with a copy of `{Target}`", entry, archive, Path.GetRelativePath(root, source));
                SafeExtraction.CopyReplacing(source, link);
            }
            else
            {
                File.Delete(link);
                logger.LogWarning("Dropping symlink `{Entry}` from archive `{Archive}`: {Reason}", entry, archive, decision.Reason);
            }
        }
    }

    /// <summary>
    /// What makes a link the same link later: where it points and which file it is. The target text alone is not enough, because
    /// an extractor can write a new link over an old one with the same text; the device and inode are null when they could not be read.
    /// </summary>
    internal readonly record struct LinkIdentity(string? Target, ulong? Device, ulong? Inode);

    /// <summary>The links in <paramref name="root"/> (see <see cref="FindLinks"/>) with their identity, taken before an archive is extracted.</summary>
    internal static Dictionary<string, LinkIdentity> Snapshot(string root) =>
        FindLinks(root).ToDictionary(link => link.Key, IdentityOf, StringComparer.Ordinal);

    private static LinkIdentity IdentityOf(FoundLink link)
    {
        // The target of a link whose name .NET cannot spell is unreadable to it; the inode still tells the link apart.
        if (!link.Nameable) return RawStat.Lstat(link.Path) is { } info ? new LinkIdentity(null, info.Device, info.Inode) : new LinkIdentity(null, null, null);

        var (device, inode) = LinkInode.Read(link.Key);
        return new LinkIdentity(new FileInfo(link.Key).LinkTarget, device, inode);
    }

    /// <summary>A link found by the raw walk: its path bytes (NUL-terminated) and the string that names it, which is the path itself when that is valid UTF-8.</summary>
    internal readonly record struct FoundLink(byte[] Path, string Key)
    {
        private const string RawPrefix = "\0raw:";

        internal bool Nameable => !Key.StartsWith(RawPrefix, StringComparison.Ordinal);

        internal static FoundLink Of(byte[] path)
        {
            try
            {
                return new FoundLink(path, new UTF8Encoding(false, true).GetString(path[..^1]));
            }
            catch (ArgumentException)
            {
                return new FoundLink(path, RawPrefix + Convert.ToBase64String(path[..^1]));
            }
        }
    }

    internal readonly record struct Decision(string? CopyFrom, string? Reason, bool TargetMissing = false);

    /// <summary>
    /// Handles the link entries an extractor read from the archive and did not create: each becomes a copy of
    /// its target when the target is a file inside <paramref name="root"/>, and is dropped with a Warning
    /// naming the entry otherwise. A file or folder already at an entry's path (an entry written through the
    /// link, is kept and the link dropped. Of several links with the same cleaned name, the last one wins. Call it after every
    /// other entry is extracted, so a target that comes later in the archive exists. A link whose target is
    /// another link entry is retried until no further link can be placed, so chains resolve in any order.
    /// </summary>
    internal static void PlaceAll(string root, string archive, IEnumerable<(string Name, string Path, string Target)> links, ILogger logger)
    {
        // Two entries that clean to one path: the later one wins.
        var all = links.ToList();
        var pending = new List<(string Name, string Path, string Target)>();
        for (var i = 0; i < all.Count; i++)
        {
            if (all.Skip(i + 1).Any(later => later.Path == all[i].Path))
            {
                logger.LogWarning("Dropping symlink `{Entry}` from archive `{Archive}`: a later entry also extracts to `{Path}`", all[i].Name, archive, all[i].Path);
            }
            else
            {
                pending.Add(all[i]);
            }
        }

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
        if (!SafeExtraction.TryCreateFolder(root, parent))
        {
            logger.LogWarning("Dropping symlink `{Entry}` from archive `{Archive}`: a folder on the way to it is a symbolic link", entry, archive);
            return PlaceOutcome.Dropped;
        }

        SafeExtraction.CopyReplacing(source, linkPath);
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

    /// <summary>
    /// The symbolic links in <paramref name="root"/> and the folders below it, without entering any link. Empty when the folder is missing.
    /// Folders are listed by name bytes, so a link .NET cannot spell is found too.
    /// </summary>
    internal static List<FoundLink> FindLinks(string root)
    {
        var links = new List<FoundLink>();
        if (!Directory.Exists(root)) return links;

        var pending = new Stack<byte[]>();
        pending.Push(RawFolderWalk.Terminated(root));
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in RawFolderWalk.Children(directory))
            {
                if (entry.Kind == RawStat.Kind.Link) links.Add(FoundLink.Of(entry.Path));
                else if (entry.Kind == RawStat.Kind.Folder) pending.Push(entry.Path);
            }
        }

        return links;
    }

    private static string TrimSeparator(string path) => path.Length > 1 ? path.TrimEnd('/') : path;

    private static bool IsInside(string path, string root) => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
}
