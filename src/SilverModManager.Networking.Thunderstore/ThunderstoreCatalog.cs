using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using SilverModManager.Networking.Thunderstore.DTOs;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.Networking.Thunderstore;

/// <summary>
/// Every package of one community, held in memory. Built from the cached listing chunks, never from the
/// database: a community lists tens of thousands of packages and none of them is a library item until installed.
/// </summary>
[PublicAPI]
public sealed class ThunderstoreCatalog
{
    private readonly Dictionary<ThunderstorePackageRef, PackageListing> _packages = new();

    public ThunderstoreCatalog(string community, IEnumerable<PackageListing> packages)
    {
        Community = community;
        // A package republished into a later chunk replaces the earlier record.
        foreach (var package in packages) _packages[RefOf(package)] = package;
    }

    public string Community { get; }

    // Keyed by the (namespace, name) pair rather than the joined full name: team names may contain dashes, so
    // two different packages can share a joined string and Resolve has to tell the splits apart.
    private static ThunderstorePackageRef RefOf(PackageListing package)
    {
        var @namespace = package.Owner ?? package.FullName[..Math.Max(0, package.FullName.Length - package.Name.Length - 1)];
        return new ThunderstorePackageRef(@namespace, package.Name);
    }

    public int Count => _packages.Count;

    public bool TryGet(ThunderstorePackageRef package, [MaybeNullWhen(false)] out PackageListing listing)
        => _packages.TryGetValue(package, out listing);

    /// <summary>
    /// The newest active version, or null when the package is unknown, has no active version, or is deprecated
    /// and <paramref name="includeDeprecated"/> is false. Versions are stored newest first.
    /// </summary>
    public PackageVersion? Latest(ThunderstorePackageRef package, bool includeDeprecated = false)
    {
        if (!TryGet(package, out var listing)) return null;
        if (listing.IsDeprecated && !includeDeprecated) return null;
        return listing.Versions.FirstOrDefault(v => v.IsActive);
    }

    /// <summary>
    /// Every version of a package, newest first, or empty when it is unknown.
    /// </summary>
    public IReadOnlyList<PackageVersion> Versions(ThunderstorePackageRef package)
        => TryGet(package, out var listing) ? listing.Versions : [];

    public IReadOnlyList<string> Categories(ThunderstorePackageRef package)
        => TryGet(package, out var listing) ? listing.Categories : [];

    /// <summary>
    /// Packages whose name or owner contains <paramref name="text"/>, case-insensitively, best rated first.
    /// </summary>
    public IReadOnlyList<PackageListing> Search(string text, bool includeDeprecated = false)
    {
        return _packages.Values
            .Where(p => includeDeprecated || !p.IsDeprecated)
            .Where(p => p.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || (p.Owner?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(p => p.RatingScore)
            .ThenBy(p => p.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Settles the namespace/name split of a dependency string by looking each candidate up.
    /// </summary>
    /// <exception cref="InvalidOperationException">No candidate split, or more than one, is a package of this community.</exception>
    public ThunderstoreVersionRef Resolve(ThunderstoreDependencyString dependency)
    {
        var matches = dependency.Candidates().Where(c => _packages.ContainsKey(c)).Take(2).ToArray();
        return matches.Length switch
        {
            1 => new ThunderstoreVersionRef(matches[0], dependency.VersionNumber),
            0 => throw new InvalidOperationException($"Dependency '{dependency}' names no package in community '{Community}'"),
            _ => throw new InvalidOperationException($"Dependency '{dependency}' is ambiguous in community '{Community}': more than one split names a package"),
        };
    }
}
