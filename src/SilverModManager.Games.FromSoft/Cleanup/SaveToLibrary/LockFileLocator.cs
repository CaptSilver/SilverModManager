using System.Collections.Immutable;

namespace SilverModManager.Games.FromSoft.Cleanup.SaveToLibrary;

/// <summary>
/// Finds and reads the <c>mods.lock.toml</c> another manager left. The manager keeps it in its own folder, not in the
/// game's, so besides the game folder and its mods folder this also looks above where the game folder's links point:
/// a manager that links its mods in lives there. Files are only read, never followed beyond that name.
/// </summary>
internal static class LockFileLocator
{
    internal const string FileName = "mods.lock.toml";

    private const int AncestorsToTry = 6;

    public static (ImmutableArray<ProvenanceEntry> Entries, ImmutableArray<string> Problems) Read(SaveScope scope)
    {
        var entries = new List<ProvenanceEntry>();
        var problems = new List<string>();
        foreach (var path in Candidates(scope).Where(File.Exists))
        {
            var text = ReadCapped(path, problems);
            if (text is null) continue;

            var list = ProvenanceReader.Read(text);
            entries.AddRange(list.Entries.Where(e => !entries.Any(known => known.Source == e.Source && known.Id == e.Id)));
            problems.AddRange(list.Problems.Select(p => $"{path}: {p}"));
        }

        return ([..entries], [..problems]);
    }

    internal static IEnumerable<string> Candidates(SaveScope scope)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var folders = new List<string> { scope.GameFolder };
        if (scope.ModsFolder is not null) folders.Add(Path.Combine(scope.GameFolder, scope.ModsFolder));

        foreach (var link in scope.Inventory.Where(e => e.Kind == InventoryKind.Link))
        {
            if (link.LinkTarget is not { } target) continue;

            var linkDirectory = Path.GetDirectoryName(Path.Combine(scope.GameFolder, link.Path)) ?? scope.GameFolder;
            var directory = Path.GetFullPath(target, linkDirectory);
            for (var i = 0; i <= AncestorsToTry && directory is not null; i++, directory = Path.GetDirectoryName(directory))
                folders.Add(directory);
        }

        foreach (var folder in folders)
        {
            var candidate = Path.GetFullPath(Path.Combine(folder, FileName));
            if (seen.Add(candidate)) yield return candidate;
        }
    }

    private static string? ReadCapped(string path, List<string> problems)
    {
        try
        {
            if (new FileInfo(path).Length > ProvenanceReader.MaxChars)
            {
                problems.Add($"{path} is too large to be a lock file and was not read.");
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{path} could not be read: {e.Message}");
            return null;
        }
    }
}
