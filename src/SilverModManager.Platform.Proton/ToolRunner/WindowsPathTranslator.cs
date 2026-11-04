using System.Text.RegularExpressions;

namespace SilverModManager.Platform.Proton.ToolRunner;

/// <summary>
/// Rewrites host paths in a command line into the form a Windows program inside a Wine prefix understands.
/// </summary>
public static partial class WindowsPathTranslator
{
    // xEdit-style options carry their value after the colon: -D:/game/data. A GNU-style --opt=/path is left alone.
    [GeneratedRegex(@"^(-[A-Za-z][\w-]*:)(/.*)$", RegexOptions.Singleline)]
    private static partial Regex FlagWithPath();

    [GeneratedRegex("\"(/[^\"]*)\"")]
    private static partial Regex QuotedPath();

    /// <summary>
    /// Translates a single command-line argument. An absolute host path under <c>{prefixRoot}/drive_c</c>
    /// becomes the matching <c>C:\</c> path, any other absolute host path becomes <c>Z:</c> plus the path,
    /// which Wine maps to the host root. A path after a <c>-X:</c> prefix, or inside double quotes
    /// anywhere in the argument, is translated the same way. <c>.</c> and <c>..</c> segments are resolved on the
    /// host path first, so <c>{prefixRoot}/drive_c/../user.reg</c> maps to the host file next to <c>drive_c</c>
    /// through <c>Z:</c>, and a <c>..</c> at the host root stays there. Everything else (flags, relative paths, plain words)
    /// is returned as is.
    /// </summary>
    /// <remarks>
    /// An argument that starts with '/' is always taken for a path, so a Windows-style switch such as <c>/c</c>
    /// cannot be passed through this helper.
    /// </remarks>
    /// <param name="argument">The argument to translate.</param>
    /// <param name="prefixRoot">The host directory that contains the prefix's <c>drive_c</c>.</param>
    public static string Translate(string argument, string prefixRoot)
    {
        if (argument.Length == 0) return argument;
        if (argument[0] == '/') return TranslatePath(argument, prefixRoot);

        var flag = FlagWithPath().Match(argument);
        if (flag.Success) return flag.Groups[1].Value + TranslatePath(flag.Groups[2].Value, prefixRoot);

        return QuotedPath().Replace(argument, m => "\"" + TranslatePath(m.Groups[1].Value, prefixRoot) + "\"");
    }

    private static string TranslatePath(string argument, string prefixRoot)
    {
        // The argument is a host path, so ".." climbs the host tree even out of drive_c: resolve first, then map.
        var resolved = ResolveDotSegments(argument);
        var driveC = ResolveDotSegments(prefixRoot.TrimEnd('/') + "/drive_c").TrimEnd('/');
        if (resolved.TrimEnd('/') == driveC) return @"C:\";
        if (resolved.StartsWith(driveC + "/", StringComparison.Ordinal))
            return "C:" + resolved[driveC.Length..].Replace('/', '\\');

        return "Z:" + resolved.Replace('/', '\\');
    }

    /// <summary>
    /// Resolves <c>.</c> and <c>..</c> lexically over a host path. A <c>..</c> at the host root stays at the root.
    /// A trailing '/' is kept on anything but the root.
    /// </summary>
    /// <param name="path">An absolute host path.</param>
    private static string ResolveDotSegments(string path)
    {
        var kept = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (kept.Count > 0) kept.RemoveAt(kept.Count - 1);
                continue;
            }

            kept.Add(segment);
        }

        var resolved = "/" + string.Join('/', kept);
        return path.EndsWith('/') && kept.Count > 0 ? resolved + "/" : resolved;
    }
}
