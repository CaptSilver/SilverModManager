namespace SilverModManager.Games.Generic.ModProvenance;

/// <summary>What a walk through the places other managers live in turned up.</summary>
internal sealed record ManagerInstalls(List<string> Mo2Inis, List<string> VortexDownloads, List<(string Root, string Origin)> ThunderstoreRoots);

/// <summary>
/// Finds other managers' data without being told where it is: Mod Organizer 2 and Fluorine (an MO2 fork, in the Flatpak
/// <c>com.fluorine.manager</c>) keep a <c>ModOrganizer.ini</c> per instance, Vortex keeps a <c>downloads</c> folder, and
/// r2modman and Gale keep per-game profile folders. A bounded walk, so a Wine prefix cannot make it crawl: links are not
/// followed, and system folders of a prefix are skipped.
/// </summary>
internal static class ManagerDiscovery
{
    internal const string Mo2IniName = "ModOrganizer.ini";

    private const int MaxDepth = 9;
    private const int MaxFolders = 30_000;

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "Program Files", "Program Files (x86)", "ProgramData", "dosdevices", "node_modules", ".cache", "steamapps", "logs", ".git",
    };

    public static IEnumerable<string> HomeRoots(string home) => home.Length == 0 ? [] :
    [
        Path.Combine(home, ".var/app/com.fluorine.manager"),
        Path.Combine(home, ".local/share/ModOrganizer"),
        Path.Combine(home, ".config/r2modmanPlus-local"),
        Path.Combine(home, ".local/share/com.kesomannen.gale"),
        Path.Combine(home, ".config/com.kesomannen.gale"),
    ];

    public static ManagerInstalls Find(ManagerSearch search)
    {
        var found = new ManagerInstalls([], [], []);
        var visited = 0;
        foreach (var root in HomeRoots(search.Home).Concat(search.ExtraRoots).Select(Path.GetFullPath).Distinct())
        {
            if (Directory.Exists(root)) Walk(root, 0, found, ref visited);
        }

        return found;
    }

    private static void Walk(string directory, int depth, ManagerInstalls found, ref int visited)
    {
        if (depth > MaxDepth || ++visited > MaxFolders) return;

        var name = Path.GetFileName(directory);
        if (name.Equals("r2modmanPlus-local", StringComparison.OrdinalIgnoreCase))
        {
            found.ThunderstoreRoots.Add((directory, "r2modman"));
            return;
        }

        if (name.Equals("com.kesomannen.gale", StringComparison.OrdinalIgnoreCase))
        {
            found.ThunderstoreRoots.Add((directory, "Gale"));
            return;
        }

        var ini = Path.Combine(directory, Mo2IniName);
        if (File.Exists(ini))
        {
            found.Mo2Inis.Add(ini);
            return;
        }

        if (name.Equals("Vortex", StringComparison.OrdinalIgnoreCase) && Path.Combine(directory, "downloads") is var downloads && Directory.Exists(downloads))
        {
            found.VortexDownloads.Add(downloads);
            return;
        }

        foreach (var child in Children(directory))
        {
            if (!Skipped.Contains(Path.GetFileName(child))) Walk(child, depth + 1, found, ref visited);
        }
    }

    internal static IEnumerable<string> Children(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory)
                .Where(d => !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
