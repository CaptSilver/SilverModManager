using NexusMods.Paths;
using NexusMods.Paths.Utilities;

namespace SilverModManager.Library;

/// <summary>
/// Which files inside an archive the library opens in turn. Anything else, a Bethesda archive for one, is kept as the
/// plain file it is, so nothing is recorded below it.
/// </summary>
public static class NestedArchives
{
    private static readonly Extension[] Extensions =
    [
        KnownExtensions._7z, KnownExtensions.Rar, KnownExtensions.Zip, KnownExtensions._7zip,
        // Stardew Valley SMAPI nested archive
        new(".dat"),
    ];

    /// <summary>Whether a file with this name inside an archive can be expanded into rows of its own.</summary>
    public static bool MayBeExpanded(RelativePath name) => Extensions.Contains(name.Extension);

    /// <inheritdoc cref="MayBeExpanded(RelativePath)"/>
    public static bool MayBeExpanded(AbsolutePath file) => Extensions.Contains(file.Extension);
}
