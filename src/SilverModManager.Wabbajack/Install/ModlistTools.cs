using NexusMods.Paths;
using SilverModManager.Wabbajack.Mapping;

namespace SilverModManager.Wabbajack.Install;

/// <summary>A program from a list's ModOrganizer.ini, ready to be stored as a <c>UserTool</c>: host paths only.</summary>
public sealed record ModlistTool(string Name, string Exe, string Arguments);

/// <summary>
/// Picks the programs of a list's <c>[customExecutables]</c> that can be run as tools: the ones whose binary is a file in
/// the list's tools folder or in the game folder, or one the list's own files will put in the game folder when the
/// loadout is applied.
/// </summary>
public static class ModlistTools
{
    // Mod Organizer and its helpers mean nothing here, and the game and its launcher already have a Play button.
    private static readonly string[] AlwaysLeftOut = ["ModOrganizer.exe", "nxmhandler.exe", "Explorer++.exe"];

    public sealed record Result(IReadOnlyList<ModlistTool> Tools, IReadOnlyList<string> Unregistered);

    /// <param name="iniText">ModOrganizer.ini as the compiler wrote it, markers included.</param>
    /// <param name="extrasRoot">The list's tools folder; what <c>MO2_PATH</c> stands for.</param>
    /// <param name="gameRoot">The game folder.</param>
    /// <param name="primaryExecutable">File name of the game's executable.</param>
    /// <param name="appliedPath">
    /// Where a file the list's loadout deploys ends up on disk, given its path inside the instance (<c>Stock Game/x.exe</c>,
    /// <c>mods/Mod/x.exe</c>), or null when the loadout does not deploy it. Those files are not on disk until Apply, so the
    /// path the ini names under the tools folder would never exist.
    /// </param>
    public static Result Find(string iniText, AbsolutePath extrasRoot, AbsolutePath gameRoot, string primaryExecutable, Func<string, string?> appliedPath)
    {
        var values = new PathMarkerValues(gameRoot.ToString(), extrasRoot.ToString(), extrasRoot.Combine("downloads").ToString());
        var leftOut = new HashSet<string>(AlwaysLeftOut, StringComparer.OrdinalIgnoreCase) { primaryExecutable };
        leftOut.Add(System.IO.Path.GetFileNameWithoutExtension(primaryExecutable) + "Launcher.exe");

        var tools = new List<ModlistTool>();
        var unregistered = new List<string>();
        foreach (var entry in Mo2Ini.CustomExecutables(Mo2Ini.Parse(iniText)))
        {
            var binary = PathMarkers.Substitute(entry.Binary, values, nativeSpelling: true);
            var fileName = binary[(binary.LastIndexOfAny(['/', '\\']) + 1)..];
            if (leftOut.Contains(fileName)) continue;

            var exe = FindFile(binary, extrasRoot) ?? FindFile(binary, gameRoot) ?? Deployed(binary, extrasRoot, appliedPath);
            if (exe is null)
            {
                unregistered.Add($"'{entry.Title}' runs '{binary}', which is not a file in the list's tools folder or the game folder");
                continue;
            }

            tools.Add(new ModlistTool(entry.Title, exe, PathMarkers.Substitute(entry.Arguments, values, nativeSpelling: true)));
        }
        return new Result(tools, unregistered);
    }

    private static string? Deployed(string binary, AbsolutePath extrasRoot, Func<string, string?> appliedPath)
    {
        var root = System.IO.Path.GetFullPath(extrasRoot.ToString()).TrimEnd('/') + "/";
        if (!System.IO.Path.IsPathRooted(binary)) return null;

        var full = System.IO.Path.GetFullPath(binary);
        return full.StartsWith(root, StringComparison.Ordinal) ? appliedPath(full[root.Length..]) : null;
    }

    /// <summary>
    /// The existing file <paramref name="path"/> names inside <paramref name="root"/>, or null. Windows lists spell paths
    /// in whatever case the author typed, so a name that differs only in case still finds the file.
    /// </summary>
    private static string? FindFile(string path, AbsolutePath root)
    {
        if (!System.IO.Path.IsPathRooted(path)) return null;

        var rootPath = System.IO.Path.GetFullPath(root.ToString()).TrimEnd('/');
        var full = System.IO.Path.GetFullPath(path);
        if (!full.StartsWith(rootPath + "/", StringComparison.Ordinal)) return null;
        if (File.Exists(full)) return full;

        var current = rootPath;
        var segments = full[(rootPath.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (!Directory.Exists(current)) return null;
            var match = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(entry => string.Equals(System.IO.Path.GetFileName(entry), segments[i], StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            current = match;
        }
        return File.Exists(current) ? current : null;
    }
}
