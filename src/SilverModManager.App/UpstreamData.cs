using NexusMods.Paths;

namespace SilverModManager.App;

/// <summary>
/// Detects data left by the upstream Nexus Mods App, which this fork can run next to. The fork
/// never reads or writes that data; the check only exists so the log can warn that both apps
/// would treat a managed game folder as their own.
/// </summary>
public static class UpstreamData
{
    public static AbsolutePath DataModelPath(IFileSystem fileSystem)
    {
        return fileSystem.GetKnownPath(KnownPath.XDG_DATA_HOME).Combine("NexusMods.App/DataModel");
    }

    public static bool Exists(IFileSystem fileSystem) => DataModelPath(fileSystem).DirectoryExists();
}
