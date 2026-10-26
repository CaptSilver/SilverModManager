namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>A file that packages of two mods both provide and the app does not merge.</summary>
/// <param name="RelPath">The path below the package folder.</param>
/// <param name="Winner">The copy me3 mounts: the one from the package it lists last.</param>
/// <param name="Losers">The copies of the other packages, one per package.</param>
public sealed record SharedPackageFile(string RelPath, ProviderFile Winner, IReadOnlyList<ProviderFile> Losers);

public static class SharedPackageFiles
{
    /// <summary>
    /// The paths two packages share whose copies me3 serves whole from the last package, in path order. A path with a
    /// merge strategy is left out: its copies go into the app's merged package, so no package's copy wins on its own.
    /// </summary>
    public static IReadOnlyList<SharedPackageFile> Of(MergePathIndex index)
    {
        var shared = new List<SharedPackageFile>();
        foreach (var candidate in index.Candidates)
        {
            if (candidate.Strategy is not null) continue;

            var winner = index.WinnerOf(candidate);
            var losers = candidate.Contributors
                .Where(f => !f.PackageId.Equals(winner.PackageId, StringComparison.Ordinal))
                .DistinctBy(f => f.PackageId, StringComparer.Ordinal)
                .ToArray();
            if (losers.Length == 0) continue;

            shared.Add(new SharedPackageFile(candidate.Path, winner, losers));
        }

        return shared;
    }
}
