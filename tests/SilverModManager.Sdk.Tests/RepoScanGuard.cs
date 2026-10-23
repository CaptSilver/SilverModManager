using FluentAssertions;

namespace NexusMods.Sdk.Tests;

/// <summary>
/// Shared plumbing for tests that scan the source tree for lines that must not exist unless
/// they are listed with a reason. Allowlist entries are keyed by file and exact trimmed line
/// rather than line number, so they survive unrelated edits but a new hit in an allowlisted
/// file still fails.
/// </summary>
internal static class RepoScanGuard
{
    private static readonly string[] SkippedFolders = ["obj", "bin", "extern", ".git"];

    /// <summary>
    /// Third-party libraries kept as source. They keep upstream's code and style, so the guards
    /// that police first-party code do not apply to them. Their namespaces follow the first-party
    /// rename, so the namespace guard passes includeVendored to scan them anyway.
    /// </summary>
    private static readonly string[] VendoredFolders =
    [
        "src/NexusMods.MnemonicDB/",
        "src/NexusMods.MnemonicDB.Abstractions/",
        "src/NexusMods.MnemonicDB.SourceGenerator/",
    ];

    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, FirstPartyNames.SolutionFile))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"Could not find {FirstPartyNames.SolutionFile} above the test binaries");
    }

    /// <summary>
    /// Files below <paramref name="directory"/> (relative to the repo root) that match one of
    /// the <paramref name="searchPatterns"/>, as forward-slash paths relative to the root.
    /// Build output and vendored folders are skipped.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string root, string directory, params string[] searchPatterns)
        => EnumerateFiles(root, directory, includeVendored: false, searchPatterns);

    /// <summary>
    /// Like the overload without the flag, but with <paramref name="includeVendored"/> the vendored
    /// libraries are scanned too. The vendored sources follow the first-party namespace rename, so a
    /// guard about that must not skip them.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string root, string directory, bool includeVendored, params string[] searchPatterns)
    {
        foreach (var searchPattern in searchPatterns)
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, directory), searchPattern, SearchOption.AllDirectories))
            {
                var relative = ToRelative(root, path);
                if (relative.Split('/').Any(part => SkippedFolders.Contains(part))) continue;
                if (!includeVendored && VendoredFolders.Any(folder => relative.StartsWith(folder, StringComparison.Ordinal))) continue;
                yield return relative;
            }
        }
    }

    public static string ToRelative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>
    /// Every (file, trimmed line) pair among <paramref name="relativeFiles"/> for which
    /// <paramref name="isHit"/> returns true.
    /// </summary>
    public static HashSet<(string File, string Line)> FindLines(string root, IEnumerable<string> relativeFiles, Func<string, bool> isHit)
    {
        var found = new HashSet<(string File, string Line)>();
        foreach (var relative in relativeFiles)
        {
            foreach (var line in File.ReadLines(Path.Combine(root, relative)))
            {
                var trimmed = line.Trim();
                if (isHit(trimmed)) found.Add((relative, trimmed));
            }
        }

        return found;
    }

    /// <summary>
    /// Fails on hits that are not allowlisted, and on allowlist entries that no longer match a line.
    /// </summary>
    public static void AssertMatchesAllowlist(
        HashSet<(string File, string Line)> found,
        IEnumerable<(string File, string Line, string Reason)> allowlist,
        string unlistedBecause)
    {
        var allowed = allowlist.Select(a => (a.File, a.Line)).ToHashSet();

        var unlisted = found.Except(allowed).OrderBy(x => x.File).Select(x => $"{x.File}: {x.Line}");
        string.Join(Environment.NewLine, unlisted).Should().BeEmpty(unlistedBecause);

        var stale = allowed.Except(found).OrderBy(x => x.File).Select(x => $"{x.File}: {x.Line}");
        string.Join(Environment.NewLine, stale).Should().BeEmpty("allowlist entries must match a scanned line, remove the ones that no longer do");
    }
}
