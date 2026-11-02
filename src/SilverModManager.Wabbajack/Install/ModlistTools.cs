using NexusMods.Paths;
using SilverModManager.Sdk.IO;
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
    /// <param name="skipReason">
    /// Why the install did not produce the file at a path inside the instance, or null when it was not skipped. Only used
    /// to say so when a tool is left out: a file the install skipped is the usual reason its binary is missing.
    /// </param>
    public static Result Find(string iniText, AbsolutePath extrasRoot, AbsolutePath gameRoot, string primaryExecutable,
        Func<string, string?> appliedPath, Func<string, string?> skipReason)
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
                var why = InsideTools(binary, extrasRoot) is { } relative ? skipReason(relative) : null;
                unregistered.Add($"'{entry.Title}' runs '{binary}', which is not a file in the list's tools folder or the game folder"
                    + (why is null ? "" : $"; the install skipped it: {why}"));
                continue;
            }

            tools.Add(new ModlistTool(entry.Title, exe, PathMarkers.Substitute(entry.Arguments, values, nativeSpelling: true)));
        }
        return new Result(tools, unregistered);
    }

    private static string? Deployed(string binary, AbsolutePath extrasRoot, Func<string, string?> appliedPath)
    {
        return InsideTools(binary, extrasRoot) is { } relative ? appliedPath(relative) : null;
    }

    /// <summary>
    /// The folder with every symlink on its path resolved. The ini spells paths one way and the folders come from
    /// elsewhere, and <c>/home</c> is a link to <c>/var/home</c> on an ostree system, so a lexical comparison would
    /// call a tool outside the folder it is in.
    /// </summary>
    private static string ResolvedRoot(AbsolutePath root)
    {
        var full = System.IO.Path.GetFullPath(root.ToString()).TrimEnd('/');
        return (LinkResolution.RealPath(full) ?? full).TrimEnd('/');
    }

    /// <summary>
    /// The file's path with the links on its folders resolved. The file itself is left alone, so a tool that is a link
    /// to somewhere else still counts as being where the list put it.
    /// </summary>
    private static string ResolvedFile(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var folder = System.IO.Path.GetDirectoryName(full);
        if (folder is null || LinkResolution.RealPath(folder) is not { } resolved) return full;
        return System.IO.Path.Combine(resolved, System.IO.Path.GetFileName(full));
    }

    /// <summary>The path of <paramref name="binary"/> inside the tools folder, or null when it is elsewhere.</summary>
    private static string? InsideTools(string binary, AbsolutePath extrasRoot)
    {
        var root = ResolvedRoot(extrasRoot) + "/";
        if (!System.IO.Path.IsPathRooted(binary)) return null;

        var full = ResolvedFile(binary);
        return full.StartsWith(root, StringComparison.Ordinal) ? full[root.Length..] : null;
    }

    /// <summary>
    /// The existing file <paramref name="path"/> names inside <paramref name="root"/>, or null. Windows lists spell paths
    /// in whatever case the author typed, so a name that differs only in case still finds the file.
    /// </summary>
    private static string? FindFile(string path, AbsolutePath root)
    {
        if (!System.IO.Path.IsPathRooted(path)) return null;

        var rootPath = ResolvedRoot(root);
        var full = ResolvedFile(path);
        if (!full.StartsWith(rootPath + "/", StringComparison.Ordinal)) return null;
        // The file is reported in the spelling the ini gave it, which is also how the folder is recorded.
        var spelled = System.IO.Path.GetFullPath(path);
        if (File.Exists(spelled)) return spelled;

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
