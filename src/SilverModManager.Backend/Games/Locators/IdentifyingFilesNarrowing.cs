using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Games.Locators;

/// <summary>
/// Tells games that launch the same executable apart by the files only their install holds.
/// </summary>
internal static class IdentifyingFilesNarrowing
{
    /// <summary>
    /// Drops the games that name identifying files the folder lacks. Games that name files and have them win over
    /// games that name none, so an Enderal folder is not also a Skyrim one.
    /// </summary>
    public static List<T> Narrow<T>(AbsolutePath folder, List<T> candidates, Func<T, IGameData> gameOf)
    {
        IReadOnlyList<RelativePath> Declared(T entry) => (gameOf(entry) as IGameIdentifyingFiles)?.IdentifyingFiles ?? [];

        var remaining = candidates
            .Where(entry => Declared(entry).All(file => FindIgnoringCase(folder, file) is not null))
            .ToList();

        var identified = remaining.Where(entry => Declared(entry).Count > 0).ToList();
        return identified.Count > 0 ? identified : remaining.Where(entry => Declared(entry).Count == 0).ToList();
    }

    /// <summary>
    /// The file at <paramref name="file"/> below <paramref name="folder"/>, matching each name exactly first and
    /// ignoring case otherwise: a Windows game's files are spelled however its installer spelled them.
    /// </summary>
    internal static AbsolutePath? FindIgnoringCase(AbsolutePath folder, RelativePath file)
    {
        var current = folder;
        var parts = file.Parts.ToArray();
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            var exact = current.Combine(parts[i]);
            if (last ? exact.FileExists : exact.DirectoryExists())
            {
                current = exact;
                continue;
            }

            if (!current.DirectoryExists()) return null;
            // Only this folder's own entries: the listing is recursive by default, and a same-named file deeper
            // down (a backup copy, a bundled tool) is not the one the game launches.
            var entries = last ? current.EnumerateFiles("*", recursive: false) : current.EnumerateDirectories("*", recursive: false);
            var name = parts[i].ToString();
            var matches = entries.Where(entry => string.Equals(entry.FileName, name, StringComparison.OrdinalIgnoreCase)).Take(1).ToArray();
            if (matches.Length == 0) return null;
            current = matches[0];
        }

        return current;
    }
}
