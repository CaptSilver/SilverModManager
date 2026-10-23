using JetBrains.Annotations;

namespace SilverModManager.Sdk.Thunderstore;

/// <summary>
/// One published version of a Thunderstore package.
/// </summary>
[PublicAPI]
public readonly record struct ThunderstoreVersionRef(ThunderstorePackageRef Package, ThunderstoreVersionNumber Version)
{
    internal const string ArchiveExtension = ".zip";

    /// <summary>
    /// The name Thunderstore gives the package zip: <c>Namespace-Name-Version.zip</c>.
    /// </summary>
    public string ArchiveFileName => $"{this}{ArchiveExtension}";

    /// <summary>
    /// Parses an archive file name back into a ref. Fails when the name has more than one dash before the
    /// version, since the namespace/name split then needs a lookup; use
    /// <see cref="ThunderstoreDependencyString.TryParseArchiveFileName"/> for those.
    /// </summary>
    public static bool TryParseArchiveFileName(string fileName, out ThunderstoreVersionRef versionRef)
    {
        versionRef = default;
        return ThunderstoreDependencyString.TryParseArchiveFileName(fileName, out var dependency) && dependency.AsVersionRef(out versionRef);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Package}-{Version}";
}
