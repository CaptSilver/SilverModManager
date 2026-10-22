using JetBrains.Annotations;

namespace NexusMods.Sdk.Thunderstore;

/// <summary>
/// A dependency string from a package manifest: <c>{team}-{package}-{version}</c>, with the namespace/name
/// boundary not yet decided.
/// </summary>
/// <remarks>
/// Team names may contain hyphens and package names may not, but a manifest is not trusted to follow that, so
/// the string splits unambiguously only at its last dash. Which earlier dash separates team from package is
/// settled by looking the <see cref="Candidates"/> up, in order.
/// </remarks>
[PublicAPI]
public readonly record struct ThunderstoreDependencyString(string UnsplitName, ThunderstoreVersionNumber VersionNumber)
{
    /// <summary>
    /// Parses a dependency string. The version must be three numeric parts and both sides of the dash before
    /// it must be non-empty.
    /// </summary>
    public static bool TryParse(string text, out ThunderstoreDependencyString dependency)
    {
        dependency = default;

        var lastDash = text.LastIndexOf('-');
        if (lastDash <= 0) return false;
        if (!ThunderstoreVersionNumber.TryParse(text.AsSpan(lastDash + 1), out var version)) return false;

        var unsplit = text[..lastDash];
        // Needs at least team and package: one inner dash that is neither first nor last.
        var firstDash = unsplit.IndexOf('-');
        if (firstDash <= 0 || unsplit[^1] == '-') return false;
        if (unsplit.Contains("--", StringComparison.Ordinal)) return false;

        dependency = new ThunderstoreDependencyString(unsplit, version);
        return true;
    }

    /// <summary>
    /// Parses the file name of a package zip, which is a dependency string plus <c>.zip</c>.
    /// </summary>
    public static bool TryParseArchiveFileName(string fileName, out ThunderstoreDependencyString dependency)
    {
        dependency = default;
        if (!fileName.EndsWith(ThunderstoreVersionRef.ArchiveExtension, StringComparison.OrdinalIgnoreCase)) return false;
        return TryParse(fileName[..^ThunderstoreVersionRef.ArchiveExtension.Length], out dependency);
    }

    /// <summary>
    /// Every way to split <see cref="UnsplitName"/> into namespace and name, shortest namespace first.
    /// </summary>
    public IEnumerable<ThunderstorePackageRef> Candidates()
    {
        var name = UnsplitName;
        for (var dash = name.IndexOf('-'); dash >= 0; dash = name.IndexOf('-', dash + 1))
        {
            yield return new ThunderstorePackageRef(name[..dash], name[(dash + 1)..]);
        }
    }

    /// <summary>
    /// Resolves to a version ref when only one split is valid (the name part has no dash). Fails when the
    /// split is ambiguous or the parts use characters Thunderstore does not allow.
    /// </summary>
    public bool AsVersionRef(out ThunderstoreVersionRef versionRef)
    {
        versionRef = default;
        var candidates = Candidates().Take(2).ToArray();
        if (candidates.Length != 1) return false;
        if (!ThunderstorePackageRef.TryCreate(candidates[0].Namespace, candidates[0].Name, out var package)) return false;

        versionRef = new ThunderstoreVersionRef(package, VersionNumber);
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => $"{UnsplitName}-{VersionNumber}";
}
