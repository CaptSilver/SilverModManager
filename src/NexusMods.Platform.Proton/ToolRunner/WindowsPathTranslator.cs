namespace NexusMods.Platform.Proton.ToolRunner;

/// <summary>
/// Rewrites host paths in a command line into the form a Windows program inside a Wine prefix understands.
/// </summary>
public static class WindowsPathTranslator
{
    /// <summary>
    /// Translates a single command-line argument. An absolute host path under <c>{prefixRoot}/drive_c</c>
    /// becomes the matching <c>C:\</c> path, any other absolute host path becomes <c>Z:</c> plus the path,
    /// which Wine maps to the host root. Everything else (flags, relative paths, plain words) is returned as is.
    /// </summary>
    /// <remarks>
    /// An argument that starts with '/' is always taken for a path, so a Windows-style switch such as <c>/c</c>
    /// cannot be passed through this helper.
    /// </remarks>
    /// <param name="argument">The argument to translate.</param>
    /// <param name="prefixRoot">The host directory that contains the prefix's <c>drive_c</c>.</param>
    public static string Translate(string argument, string prefixRoot)
    {
        if (argument.Length == 0 || argument[0] != '/') return argument;

        var driveC = prefixRoot.TrimEnd('/') + "/drive_c";
        if (argument == driveC) return @"C:\";
        if (argument.StartsWith(driveC + "/", StringComparison.Ordinal))
            return @"C:" + argument[driveC.Length..].Replace('/', '\\');

        return "Z:" + argument.Replace('/', '\\');
    }
}
