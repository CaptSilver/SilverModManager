using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SilverModManager.Games.Generic.ModProvenance;

public sealed record ArchiveMatch(string Path, ArchiveHit Hit);

public sealed record ArchiveSearch(ImmutableArray<ArchiveMatch> Matches, ImmutableArray<string> Problems);

/// <summary>
/// Finds out which Nexus Mods files the archives in some folders are, by MD5 in one batched lookup. Only archive files
/// are hashed, and only in the folders given: Nexus Mods knows whole uploads, so the loose files of a game folder would
/// never match and are never read.
/// </summary>
public static partial class ArchiveIdentifier
{
    private const int MaxArchives = 500;
    private const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxDepth = 2;

    /// <summary>How much is hashed in all. The cleanup's confirm list waits for this, and a Downloads folder can hold hundreds of gigabytes.</summary>
    public const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".zip", ".7z", ".rar" };

    [GeneratedRegex(@"^(?<name>.+?) \d{1,10} \S+ \d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z")]
    private static partial Regex NewStyle();

    [GeneratedRegex(@"^(?<name>.+?)-\d{1,10}-\d+(?:-\d+)*-\d{9,11}$")]
    private static partial Regex OldStyle();

    /// <param name="wantedMods">The mods being identified. Archives named like one of them are hashed first, so the budget drops the unlikely ones.</param>
    /// <param name="byteBudget">How many bytes are hashed at most; archives past it are skipped and reported.</param>
    public static async Task<ArchiveSearch> FindAsync(
        IArchiveCatalog catalog,
        uint game,
        IEnumerable<string> folders,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? wantedMods = null,
        long byteBudget = MaxTotalBytes)
    {
        var problems = ImmutableArray.CreateBuilder<string>();
        var archives = WithinBudget(Archives(folders, problems), wantedMods ?? [], byteBudget, problems);
        if (archives.Count == 0) return new ArchiveSearch([], problems.ToImmutable());

        try
        {
            var byHash = new Dictionary<string, string>();
            foreach (var archive in archives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await HashAsync(archive, problems, cancellationToken) is { } md5) byHash.TryAdd(md5, archive);
            }

            if (byHash.Count == 0) return new ArchiveSearch([], problems.ToImmutable());

            var hits = await catalog.LookupArchivesAsync([..byHash.Keys], cancellationToken);
            var matches = hits
                .Where(h => h.GameId == game && byHash.ContainsKey(h.Md5.ToLowerInvariant()))
                .Select(h => new ArchiveMatch(byHash[h.Md5.ToLowerInvariant()], h))
                .ToImmutableArray();
            return new ArchiveSearch(matches, problems.ToImmutable());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            problems.Add($"Nexus Mods could not be asked about the archives: {e.Message}");
            return new ArchiveSearch([], problems.ToImmutable());
        }
    }

    /// <summary>The mod's name as an archive name spells it: everything before the mod id, in either of Nexus Mods' two naming shapes.</summary>
    public static string NamePart(string archiveFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(archiveFileName);
        if (NewStyle().Match(stem) is { Success: true } modern) return modern.Groups["name"].Value;
        return OldStyle().Match(stem) is { Success: true } old ? old.Groups["name"].Value : stem;
    }

    private static List<string> WithinBudget(List<(string Path, long Size)> archives, IReadOnlyCollection<string> wantedMods, long byteBudget, ImmutableArray<string>.Builder problems)
    {
        var wanted = wantedMods.Select(Names.Squash).Where(n => n.Length > 0).ToHashSet();
        var ordered = archives.OrderBy(a => wanted.Contains(Names.Squash(NamePart(Path.GetFileName(a.Path)))) ? 0 : 1);

        var chosen = new List<string>();
        var left = byteBudget;
        var skipped = 0;
        foreach (var (path, size) in ordered)
        {
            if (size > left)
            {
                skipped++;
                continue;
            }

            left -= size;
            chosen.Add(path);
        }

        if (skipped > 0)
            problems.Add($"{skipped} archive{(skipped == 1 ? " was" : "s were")} not checked, to keep the hashing under {byteBudget >> 20} MiB; archives named like the mods were checked first.");
        return chosen;
    }

    private static List<(string Path, long Size)> Archives(IEnumerable<string> folders, ImmutableArray<string>.Builder problems)
    {
        var found = new List<(string Path, long Size)>();
        foreach (var folder in folders.Select(Path.GetFullPath).Distinct())
        {
            if (!Directory.Exists(folder)) continue;
            Collect(folder, 0, found, problems);
            if (found.Count >= MaxArchives)
            {
                problems.Add($"More than {MaxArchives} archives were found; only the first {MaxArchives} were checked.");
                return found;
            }
        }

        return found;
    }

    private static void Collect(string folder, int depth, List<(string Path, long Size)> found, ImmutableArray<string>.Builder problems)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
            {
                if (found.Count >= MaxArchives) return;
                if (!Extensions.Contains(Path.GetExtension(file))) continue;
                var size = new FileInfo(file).Length;
                if (size > MaxArchiveBytes)
                {
                    problems.Add($"{file} is larger than {MaxArchiveBytes >> 30} GiB and was not checked.");
                    continue;
                }

                found.Add((file, size));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{folder} could not be read: {e.Message}");
            return;
        }

        if (depth >= MaxDepth) return;
        foreach (var child in ManagerDiscovery.Children(folder)) Collect(child, depth + 1, found, problems);
    }

    private static async Task<string?> HashAsync(string path, ImmutableArray<string>.Builder problems, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            return Convert.ToHexStringLower(await MD5.HashDataAsync(stream, cancellationToken));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{path} could not be read: {e.Message}");
            return null;
        }
    }
}
