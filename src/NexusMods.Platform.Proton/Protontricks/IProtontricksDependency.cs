using NexusMods.Paths;

namespace NexusMods.Platform.Proton.Protontricks;

/// <summary>
/// Common interface for invoking Protontricks across the <see cref="ProtontricksFlatpakDependency"/> and <see cref="ProtontricksNativeDependency"/>.
/// </summary>
public interface IProtontricksDependency
{
    /// <summary>
    /// The command line that runs <paramref name="exe"/> with <paramref name="args"/> in the prefix of the Steam
    /// app, through <c>protontricks-launch</c>.
    /// </summary>
    IReadOnlyList<string> MakeLaunchArgv(long appId, AbsolutePath exe, IReadOnlyList<string> args);
}
