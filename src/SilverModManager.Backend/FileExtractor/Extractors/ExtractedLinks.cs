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
        var decisions = links.Select(link => (Link: link, Decision: Judge(link, lexicalRoot, realRoot))).ToArray();
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

    private readonly record struct Decision(string? CopyFrom, string? Reason);

    private static Decision Judge(string link, string lexicalRoot, string realRoot)
    {
        var target = new FileInfo(link).LinkTarget ?? throw new InvalidOperationException($"`{link}` is not a symbolic link");
        var directory = Path.GetDirectoryName(link) ?? throw new InvalidOperationException($"`{link}` has no parent directory");

        if (!IsInside(Path.GetFullPath(target, directory), lexicalRoot)) return new Decision(null, $"it points outside the extraction folder (`{target}`)");

        var real = LinkResolution.RealPath(Path.Combine(directory, target));
        if (real is null) return new Decision(null, $"`{target}` is part of a link loop");
        if (!IsInside(real, realRoot)) return new Decision(null, $"`{target}` resolves outside the extraction folder");

        // Real files only: a folder link would hand the library a tree it never extracted.
        return File.Exists(real) ? new Decision(real, null) : new Decision(null, $"`{target}` is not a file inside the archive");
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
