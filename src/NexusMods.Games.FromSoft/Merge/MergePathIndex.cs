using NexusMods.Games.FromSoft.Formats;
using NexusMods.Hashing.xxHash3;

namespace NexusMods.Games.FromSoft.Merge;

/// <summary>One enabled file under a package folder of the mods location.</summary>
/// <param name="PackageId">The package folder.</param>
/// <param name="RelPath">The path below the package folder, as the package spells it.</param>
/// <param name="Hash">The file's content hash.</param>
/// <param name="Priority">The loadout priority of the mod that provides it; higher wins.</param>
/// <param name="ModName">The mod's name for display.</param>
public readonly record struct ProviderFile(string PackageId, string RelPath, Hash Hash, ulong Priority, string ModName);

/// <summary>Whether the app will merge a path, and if not, why.</summary>
public readonly record struct MergeOutcome(bool Merges, string? Reason)
{
    public const string NoStrategy = "not a file the app merges";
    public const string NoVanillaSource = "needs the vanilla file the mods branched from; add an archive holding a vanilla copy to the library";
    public const string CaseOnlyCollision = "these paths differ only in case, so it's ambiguous which file me3 would mount; rename one so the paths differ for real, or drop one of the mods";

    public static MergeOutcome WillMerge => new(true, null);

    public static MergeOutcome NotMergeable(string reason) => new(false, reason);
}

/// <summary>A path more than one mod provides, or that one mod provides under several spellings.</summary>
/// <param name="Path">The highest-priority contributor's spelling.</param>
/// <param name="Spellings">Every distinct spelling, in ordinal order.</param>
/// <param name="Strategy">What the engine would use; null when it has none.</param>
/// <param name="Contributors">Every providing file, the highest priority first.</param>
/// <param name="Outcome">Whether the merge will happen.</param>
public sealed record MergeCandidate(
    string Path,
    IReadOnlyList<string> Spellings,
    MergeStrategy? Strategy,
    IReadOnlyList<ProviderFile> Contributors,
    MergeOutcome Outcome);

/// <summary>
/// Maps each package-relative path that needs a decision to the enabled files that provide it. It is the one answer
/// the synchronizer, apply checks, Health Check, UI and CLI read, so they cannot disagree.
/// <para>
/// It replaces the planning half of erm's conflicts.py (index_paths, the case-only check, the provider-count rule)
/// for a loadout where nothing is declared: erm's <c>prefer</c> is the highest priority, <c>mods</c> is every enabled
/// provider, <c>vanilla</c> is a member of a recognised vanilla dump, a disabled mod is simply not in the input, and
/// a provider that was never declared or went stale cannot exist. Not ported: declaring merges, the refusals about
/// prefer, undeclared providers and unknown strategies, prunes, renames, faithful-source checks and stale
/// contributors. Pure and deterministic; it never touches disk or the database.
/// </para>
/// </summary>
public sealed class MergePathIndex
{
    /// <summary>
    /// The package that holds the previous apply's merge output. Its files are never providers: they came from the
    /// others, and counting them would make a merged file collide with its own inputs.
    /// </summary>
    public const string MergedPackageId = Me3.Me3ModsLayout.MergedPackageId;

    private readonly Dictionary<string, MergeCandidate> _byPath;

    /// <summary>Every candidate, sorted by path.</summary>
    public IReadOnlyList<MergeCandidate> Candidates { get; }

    private MergePathIndex(IReadOnlyList<MergeCandidate> candidates)
    {
        Candidates = candidates;
        _byPath = new Dictionary<string, MergeCandidate>(candidates.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates) _byPath[candidate.Path] = candidate;
    }

    /// <param name="files">Every enabled file under a package folder; natives and the overlay are left out by the caller.</param>
    /// <param name="traits">The game's format traits.</param>
    /// <param name="vanillaPaths">Package-relative paths some recognised vanilla dump offers a copy of.</param>
    public static MergePathIndex Build(IEnumerable<ProviderFile> files, FromSoftFormatTraits traits, IReadOnlySet<string> vanillaPaths)
    {
        var groups = new Dictionary<string, List<ProviderFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (file.PackageId.Equals(MergedPackageId, StringComparison.Ordinal)) continue;
            if (!groups.TryGetValue(file.RelPath, out var list)) groups[file.RelPath] = list = [];
            list.Add(file);
        }

        var candidates = new List<MergeCandidate>();
        foreach (var list in groups.Values)
        {
            var spellings = list.Select(f => f.RelPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var packages = list.Select(f => f.PackageId).Distinct(StringComparer.Ordinal).Count();
            if (packages < 2 && spellings.Length < 2) continue;

            var contributors = list
                .OrderByDescending(f => f.Priority)
                .ThenBy(f => f.PackageId, StringComparer.Ordinal)
                .ThenBy(f => f.RelPath, StringComparer.Ordinal)
                .ToArray();
            var path = contributors[0].RelPath;
            var vanilla = spellings.Any(vanillaPaths.Contains);
            var strategy = MergeStrategies.ForPath(path, traits, vanilla);

            MergeOutcome outcome;
            if (spellings.Length > 1) outcome = MergeOutcome.NotMergeable(MergeOutcome.CaseOnlyCollision);
            else if (strategy is not { } s) outcome = MergeOutcome.NotMergeable(MergeOutcome.NoStrategy);
            // A strategy that starts from the game's own file reads its ancestor from the game folder, which always
            // holds it; only files the game ships inside its packed archives need a vanilla dump from the library.
            else if (MergeStrategies.NeedsVanilla(s) && !MergeStrategies.TakesGameBase(s) && !vanilla) outcome = MergeOutcome.NotMergeable(MergeOutcome.NoVanillaSource);
            else outcome = MergeOutcome.WillMerge;

            candidates.Add(new MergeCandidate(path, spellings, strategy, contributors, outcome));
        }

        candidates.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new MergePathIndex(candidates);
    }

    /// <summary>The candidate for a package-relative path in any casing, or null when nothing needs a decision there.</summary>
    public MergeCandidate? Lookup(string relPath) => _byPath.GetValueOrDefault(relPath);
}
