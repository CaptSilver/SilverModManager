using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.CrashLogs;

/// <summary>Where a game's crash logs are and how to read one.</summary>
public interface ICrashLogSource
{
    /// <summary>The logs that exist now, newest first. Empty when the folder does not.</summary>
    IReadOnlyList<CrashLogFile> Find(GameInstallation installation);

    /// <summary>The text of a log. Opens the file shared, because the logger may still be writing the newest one.</summary>
    string ReadText(CrashLogFile file);
}
