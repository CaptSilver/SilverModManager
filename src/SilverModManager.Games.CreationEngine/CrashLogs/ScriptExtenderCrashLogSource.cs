using System.Text;
using NexusMods.Paths;
using SilverModManager.Games.CreationEngine.Abstractions;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.CrashLogs;

/// <summary>The <c>crash-*.log</c> files CrashLoggerSSE writes next to the script extender's own log.</summary>
public sealed class ScriptExtenderCrashLogSource(ICreationEngineGame game) : ICrashLogSource
{
    public IReadOnlyList<CrashLogFile> Find(GameInstallation installation)
    {
        if (game.ScriptExtenderLogFolder is not { } folder) return [];

        var directory = installation.Locations.ToAbsolutePath(folder);
        if (!directory.DirectoryExists()) return [];

        return directory.EnumerateFiles("crash-*.log", recursive: false)
            .Select(Describe)
            .OfType<CrashLogFile>()
            .OrderByDescending(file => file.LastWriteUtc)
            .ThenByDescending(file => file.Path.FileName.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    // A log the game or the user deletes between the listing and the stat is simply gone.
    private static CrashLogFile? Describe(AbsolutePath file)
    {
        try
        {
            return new CrashLogFile(file, file.FileInfo.LastWriteTimeUtc, file.FileInfo.Size);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public string ReadText(CrashLogFile file)
    {
        // Same sharing as the app's own log reader: the crash logger may still have the file open.
        using var stream = file.Path.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
