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
    /// anywhere in the argument, is translated the same way. <c>.</c> and <c>..</c> segments are resolved first and
    /// never climb above the drive root. Everything else (flags, relative paths, plain words)
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
        var driveC = prefixRoot.TrimEnd('/') + "/drive_c";
        if (argument == driveC) return @"C:\";
        if (argument.StartsWith(driveC + "/", StringComparison.Ordinal))
            return @"C:" + ResolveDotSegments(argument[driveC.Length..]);

        return "Z:" + ResolveDotSegments(argument);
    }

    /// <summary>
    /// Resolves <c>.</c> and <c>..</c> lexically, the way Windows would, and returns the result with backslashes.
    /// A <c>..</c> at the root stays at the root, so a path cannot climb out of the drive it is mapped onto.
    /// </summary>
    /// <param name="path">A '/'-rooted path, relative to the drive.</param>
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

        var resolved = @"\" + string.Join('\\', kept);
        return path.EndsWith('/') && kept.Count > 0 ? resolved + @"\" : resolved;
    }
}
