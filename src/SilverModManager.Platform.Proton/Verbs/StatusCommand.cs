using SilverModManager.Platform.Proton.Launch;

namespace SilverModManager.Platform.Proton.Verbs;

/// <summary>
/// <c>silvermod status [--json]</c>: shows which launches hold or have left a lock. It reads the lock and state
/// files and nothing else, so it needs no host and works while a game runs.
/// </summary>
public static class StatusCommand
{
    public const string Synopsis = "usage: silvermod status [--json]";

    /// <summary>
    /// Writes the report to <paramref name="output"/> and returns 0, or writes why it could not to
    /// <paramref name="error"/> and returns 1.
    /// </summary>
    /// <param name="args">The arguments after <c>status</c>.</param>
    /// <param name="runtimeDir"><c>XDG_RUNTIME_DIR</c>, or null when it is not set.</param>
    /// <param name="mountInfoPath">The mount table to look for virtual file system mounts in.</param>
    public static int Run(IReadOnlyList<string> args, string? runtimeDir, string mountInfoPath, TextWriter output, TextWriter error)
    {
        var json = false;
        foreach (var arg in args)
        {
            if (arg == "--json")
            {
                json = true;
                continue;
            }

            error.WriteLine($"Unknown argument '{arg}'. {Synopsis}");
            return 1;
        }

        if (string.IsNullOrEmpty(runtimeDir))
        {
            error.WriteLine("XDG_RUNTIME_DIR is not set, so there is nowhere to look for launch locks.");
            return 1;
        }

        LaunchStatusReport report;
        try
        {
            report = LaunchStatusReport.Read(runtimeDir, mountInfoPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Could not read the launch status: {e.Message}");
            return 1;
        }

        output.Write(json ? report.ToJson() + "\n" : report.ToText());
        return 0;
    }
}
