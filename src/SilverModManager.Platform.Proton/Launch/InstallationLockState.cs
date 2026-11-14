using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// The contents of an installation lock's state file.
/// </summary>
/// <param name="Pid">Process id of the lock holder.</param>
/// <param name="Loadout">Name of the loadout being launched.</param>
/// <param name="Phase">How far the launch has got.</param>
/// <param name="IsLeftover">
/// True when nobody holds the lock any more, so the file describes a launch that died without cleaning up.
/// </param>
/// <param name="Values">Every key in the file, including ones written by the wrapper.</param>
public sealed record InstallationLockState(
    int Pid,
    string Loadout,
    LaunchPhase Phase,
    bool IsLeftover,
    IReadOnlyDictionary<string, string> Values)
{
    /// <summary>
    /// The Wine DLL overrides the launch's game needs. None for a file written without the key, as older
    /// builds did.
    /// </summary>
    public IReadOnlyList<WineDllOverride> WineDllOverrides =>
        WineParser.ParseEnvironmentVariable(Values.GetValueOrDefault(InstallationLock.WineDllOverridesKey, ""));

    /// <summary>
    /// The variables the loadout adds to the launch unless it already has them. None for a file written without the
    /// key, as a launch without any and older builds do.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> LaunchEnvironment =>
        Values.GetValueOrDefault(InstallationLock.LaunchEnvironmentKey, "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Split('=', 2))
            .Select(parts => new KeyValuePair<string, string>(parts[0], parts.Length > 1 ? parts[1] : ""))
            .ToArray();

    /// <summary>
    /// The host folders the game's process must be able to see, which the window recorded for the mounts it holds. None
    /// for a file written without the key, as a launch without mounts and older builds do.
    /// </summary>
    public IReadOnlyList<string> FilesystemRoots =>
        Values.GetValueOrDefault(InstallationLock.FilesystemRootsKey, "").Split(':', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The exe and arguments the window wants the game started with. Null for a file written without them,
    /// as older builds did.
    /// </summary>
    /// <exception cref="InvalidDataException">The file has them but they are malformed.</exception>
    public RecordedLaunch? RecordedLaunch => Launch.RecordedLaunch.From(Values);
}
