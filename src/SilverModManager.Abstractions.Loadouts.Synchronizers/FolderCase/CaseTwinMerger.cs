using System.ComponentModel;
using System.IO.Enumeration;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.FolderCase;

/// <summary>
/// What a merge did, as game paths so the caller can map them to its rows.
/// </summary>
/// <param name="Moved">Files moved into the chosen spelling, as their new paths.</param>
/// <param name="Deduplicated">Files deleted because the other spelling held the same bytes, as the path that remains.</param>
/// <param name="LeftInPlace">Paths in the variant spelling that stayed, because they conflict, are protected or belong to the game.</param>
/// <param name="NeedsFreshRead">Paths whose recorded disk row may no longer describe the file there; the caller drops the row so the next index records the file.</param>
public sealed record MergeResult(
    IReadOnlyList<GamePath> Moved,
    IReadOnlyList<GamePath> Deduplicated,
    IReadOnlyList<GamePath> LeftInPlace,
    IReadOnlyList<GamePath> NeedsFreshRead);

/// <summary>
/// Merges folders whose names differ only in case, on a case-sensitive disk where they are different folders, into the
/// spelling <see cref="FolderSpellings"/> chose. It touches the disk only; it reads no database, and reports paths, not rows.
/// </summary>
/// <remarks>
/// Moves are <c>rename(2)</c>, which keeps size and modification time, so the indexer, which looks rows up without regard
/// to case, reads a moved file as unchanged. A rename that fails (read-only folder, another mount) throws; what was
/// moved before stays valid and the next apply carries on.
/// A group is left alone, and reported when it has more than one spelling, when it is filtered (saves, files the game
/// writes), game-owned, a symlink or mount point, or holds nothing the loadout deploys or has deployed.
/// </remarks>
public sealed class CaseTwinMerger(IFullFileHasher hasher, IFileStore store, ILogger logger, IGamePathFilter filter)
{
    private readonly Func<string, string, bool> _sameDirectory = IsSameDirectory;

    /// <summary>Tests replace the inode comparison, which on a case-sensitive disk is never true for two listed folders.</summary>
    internal CaseTwinMerger(IFullFileHasher hasher, IFileStore store, ILogger logger, IGamePathFilter filter, Func<string, string, bool> sameDirectory)
        : this(hasher, store, logger, filter)
        => _sameDirectory = sameDirectory;

    private readonly record struct Entry(string Name, bool IsDirectory, bool IsSymlink);

    private sealed class Run(FolderSpellings spellings, IReadOnlyDictionary<GamePath, FileIdentity> managed, CancellationToken token)
    {
        public readonly FolderSpellings Spellings = spellings;
        public readonly IReadOnlyDictionary<GamePath, FileIdentity> Managed = managed;
        public readonly CancellationToken Token = token;
        public readonly HashSet<string> ManagedFolders = ManagedFolderKeys(managed.Keys);
        public readonly List<GamePath> Moved = [];
        public readonly List<GamePath> Deduplicated = [];
        public readonly List<GamePath> LeftInPlace = [];
        public readonly List<GamePath> NeedsFreshRead = [];
    }

    /// <summary>
    /// Merges every group of case twins that is safe to merge.
    /// </summary>
    /// <param name="managed">What the loadout deploys or has deployed: every target path and the bytes wanted there (none when unknown).</param>
    public async ValueTask<MergeResult> MergeTwins(GameInstallation installation, FolderSpellings spellings, IReadOnlyDictionary<GamePath, FileIdentity> managed, CancellationToken ct)
    {
        var run = new Run(spellings, managed, ct);
        foreach (var (location, root) in installation.Locations.GetTopLevelLocations())
        {
            if (!root.DirectoryExists()) continue;
            await Walk(run, location, root.GetFullPath(), "");
        }
        return new MergeResult(run.Moved, run.Deduplicated, run.LeftInPlace, run.NeedsFreshRead);
    }

    private async ValueTask Walk(Run run, LocationId location, string abs, string rel)
    {
        run.Token.ThrowIfCancellationRequested();

        var groups = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in List(abs))
        {
            if (!entry.IsDirectory) continue;
            if (!groups.TryGetValue(entry.Name, out var members)) groups[entry.Name] = members = [];
            members.Add(entry);
        }

        foreach (var members in groups.Values.ToArray())
        {
            members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            var relFolder = Join(rel, members[0].Name);
            var path = Path(location, relFolder);
            var twins = members.Count > 1;

            if (filter.ShouldFilter(path)) continue;
            if (run.Spellings.IsGameOwned(location, relFolder) || members.Any(m => m.IsSymlink))
            {
                if (twins) Leave(run, location, rel, members);
                continue;
            }

            var canonical = LastSegment(run.Spellings.CanonicalFolder(location, relFolder));
            if (!twins && members[0].Name.Equals(canonical, StringComparison.Ordinal))
            {
                await Walk(run, location, System.IO.Path.Combine(abs, canonical), Join(rel, canonical));
                continue;
            }

            if (IsMountPoint(abs, members))
            {
                if (twins) Leave(run, location, rel, members);
                continue;
            }

            if (!run.ManagedFolders.Contains(FolderKey(location, relFolder)))
            {
                if (twins) Leave(run, location, rel, members);
                continue;
            }

            var canonicalAbs = System.IO.Path.Combine(abs, canonical);
            var canonicalRel = Join(rel, canonical);
            var variants = members.Where(m => !m.Name.Equals(canonical, StringComparison.Ordinal)).ToList();
            if (!members.Any(m => m.Name.Equals(canonical, StringComparison.Ordinal)))
            {
                // Nothing spelled right yet: the first variant takes the name, and the rest merge into it.
                Rename(System.IO.Path.Combine(abs, variants[0].Name), canonicalAbs);
                logger.LogInformation("Renamed folder '{From}' to '{To}'", System.IO.Path.Combine(abs, variants[0].Name), canonicalAbs);
                variants.RemoveAt(0);
            }

            foreach (var variant in variants) await MergeInto(run, location, System.IO.Path.Combine(abs, variant.Name), canonicalAbs, canonicalRel, Join(rel, variant.Name));
            await Walk(run, location, canonicalAbs, canonicalRel);
        }
    }

    private void Leave(Run run, LocationId location, string rel, List<Entry> members)
    {
        foreach (var member in members)
        {
            logger.LogWarning("Folders '{Folder}' differ only in case and are left as they are", Join(rel, member.Name));
            run.LeftInPlace.Add(Path(location, Join(rel, member.Name)));
        }
    }

    private async ValueTask MergeInto(Run run, LocationId location, string variantAbs, string canonicalAbs, string canonicalRel, string variantRel)
    {
        run.Token.ThrowIfCancellationRequested();

        // A folder that is the same as the canonical one under another name (a case-insensitive directory) holds each
        // file once. Enumerating and deleting here would delete the only copy.
        if (_sameDirectory(variantAbs, canonicalAbs))
        {
            logger.LogInformation("'{Variant}' and '{Canonical}' are one folder", variantAbs, canonicalAbs);
            return;
        }

        // Earlier variants moved in beside it can leave two spellings of a name here; the exact one is the match.
        var inCanonical = List(canonicalAbs).ToLookup(e => e.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in List(variantAbs))
        {
            var from = System.IO.Path.Combine(variantAbs, entry.Name);
            var childRel = Join(canonicalRel, entry.Name);
            var variantChildPath = Path(location, Join(variantRel, entry.Name));
            if (filter.ShouldFilter(Path(location, childRel)) || filter.ShouldFilter(variantChildPath)) continue;

            var candidates = inCanonical[entry.Name].ToList();
            var match = candidates.FirstOrDefault(c => c.Name.Equals(entry.Name, StringComparison.Ordinal));
            if (match == default) match = candidates.FirstOrDefault();
            if (entry.IsSymlink)
            {
                if (match == default) Move(run, location, from, System.IO.Path.Combine(canonicalAbs, entry.Name), childRel, record: false);
                else run.LeftInPlace.Add(variantChildPath);
                continue;
            }

            if (entry.IsDirectory && IsMountPoint(variantAbs, [entry]))
            {
                run.LeftInPlace.Add(variantChildPath);
                continue;
            }

            if (match == default)
            {
                Move(run, location, from, System.IO.Path.Combine(canonicalAbs, entry.Name), childRel, record: !entry.IsDirectory);
                continue;
            }

            var to = System.IO.Path.Combine(canonicalAbs, match.Name);
            if (entry.IsDirectory != match.IsDirectory || match.IsSymlink)
            {
                logger.LogWarning("'{Variant}' stays: '{Canonical}' is a {Kind}", from, to, entry.IsDirectory ? "file" : "folder");
                run.LeftInPlace.Add(variantChildPath);
                continue;
            }

            if (entry.IsDirectory)
            {
                if (entry.Name.Equals(match.Name, StringComparison.Ordinal))
                {
                    await MergeInto(run, location, from, to, Join(canonicalRel, match.Name), Join(variantRel, entry.Name));
                }
                else
                {
                    // Two spellings of a subfolder side by side; the walk of the canonical folder merges them next.
                    Move(run, location, from, System.IO.Path.Combine(canonicalAbs, entry.Name), childRel, record: false);
                }
                continue;
            }

            await ResolveFileConflict(run, location, from, to, Path(location, Join(canonicalRel, match.Name)), variantChildPath);
        }

        if (!List(variantAbs).Any()) Directory.Delete(variantAbs);
    }

    private async ValueTask ResolveFileConflict(Run run, LocationId location, string variantFile, string canonicalFile, GamePath canonicalPath, GamePath variantPath)
    {
        var variantInfo = new FileInfo(variantFile);
        var canonicalInfo = new FileInfo(canonicalFile);

        if (IsSameFile(variantFile, canonicalFile))
        {
            File.Delete(variantFile);
            run.Deduplicated.Add(canonicalPath);
            return;
        }

        var variantHash = await hasher.Hash(FileSystem.Shared.FromUnsanitizedFullPath(variantFile), run.Token);
        var canonicalHash = await hasher.Hash(FileSystem.Shared.FromUnsanitizedFullPath(canonicalFile), run.Token);
        if (variantInfo.Length == canonicalInfo.Length && variantHash.Equals(canonicalHash))
        {
            logger.LogInformation("Deleted '{Variant}': '{Canonical}' holds the same bytes", variantFile, canonicalFile);
            File.Delete(variantFile);
            run.Deduplicated.Add(canonicalPath);
            return;
        }

        // Different bytes. A copy the app can restore from its archive is not worth keeping next to the other one.
        run.NeedsFreshRead.Add(canonicalPath);
        if (run.Managed.TryGetValue(canonicalPath, out var wanted) && !wanted.IsNone && await store.HaveFile(wanted))
        {
            if (variantHash.Equals(wanted))
            {
                logger.LogInformation("Deleted '{Variant}': the app's copy is archived and '{Canonical}' differs", variantFile, canonicalFile);
                File.Delete(variantFile);
                return;
            }

            if (canonicalHash.Equals(wanted))
            {
                logger.LogInformation("Replaced '{Canonical}' with '{Variant}': the app's copy is archived", canonicalFile, variantFile);
                File.Delete(canonicalFile);
                Rename(variantFile, canonicalFile);
                run.Moved.Add(canonicalPath);
                return;
            }
        }

        logger.LogWarning("'{Variant}' stays: '{Canonical}' has different contents", variantFile, canonicalFile);
        run.LeftInPlace.Add(variantPath);
    }

    private void Move(Run run, LocationId location, string from, string to, string childRel, bool record)
    {
        Rename(from, to);
        logger.LogInformation("Moved '{From}' to '{To}'", from, to);
        if (record) run.Moved.Add(Path(location, childRel));
    }

    private static void Rename(string from, string to)
    {
        var errno = LibcLinks.TryRename(from, to);
        if (errno == 0) return;

        throw new SynchronizerPreflightException(
            $"'{from}' could not be moved to '{to}' ({new Win32Exception(errno).Message}). Folders that differ only in case have to be merged " +
            $"so the game sees all of their files. What was moved before this stays. Make the folder writable or merge the two by hand, then apply again.");
    }

    private static bool IsSameFile(string a, string b)
        => LibcStat.TryStat(a, out var first) == 0 && LibcStat.TryStat(b, out var second) == 0 && first.Device == second.Device && first.Inode == second.Inode;

    private static bool IsSameDirectory(string a, string b) => IsSameFile(a, b);

    private static bool IsMountPoint(string parent, IEnumerable<Entry> members)
    {
        if (LibcStat.TryStat(parent, out var parentStat) != 0) return false;
        foreach (var member in members)
        {
            if (LibcStat.TryLStat(System.IO.Path.Combine(parent, member.Name), out var stat) == 0 && stat.Device != parentStat.Device) return true;
        }
        return false;
    }

    private static List<Entry> List(string directory)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
        return new FileSystemEnumerable<Entry>(directory,
            (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0),
            options).ToList();
    }

    private static HashSet<string> ManagedFolderKeys(IEnumerable<GamePath> managed)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in managed)
        {
            var text = path.Path.ToString();
            for (var slash = text.IndexOf('/'); slash >= 0; slash = text.IndexOf('/', slash + 1)) keys.Add(FolderKey(path.LocationId, text[..slash]));
        }
        return keys;
    }

    private static string FolderKey(LocationId location, string folder) => location + "|" + folder;
    private static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;
    private static string LastSegment(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static GamePath Path(LocationId location, string rel) => new(location, RelativePath.FromUnsanitizedInput(rel));
}
