using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// The rule most games share for files they write while they run: logs and crash dumps. A file like this is
/// never a mod, so it should not be offered to the user as one.
/// </summary>
public static class GameWrittenFiles
{
    private static readonly string[] Extensions = [".log", ".dmp"];
    private static readonly string[] Folders = ["logs", "crashes", "CrashDumps"];

    /// <summary>
    /// True for a <c>.log</c> or <c>.dmp</c> file, and for anything inside a folder named <c>logs</c>,
    /// <c>crashes</c> or <c>CrashDumps</c> (case-insensitive).
    /// </summary>
    public static bool IsLogOrCrashFile(GamePath path)
    {
        var parts = path.Path.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        var fileName = parts[^1];
        foreach (var extension in Extensions)
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;
        }

        for (var i = 0; i < parts.Length - 1; i++)
        {
            foreach (var folder in Folders)
            {
                if (parts[i].Equals(folder, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }
}
