using DynamicData.Kernel;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Games.Versions;

/// <summary>
/// Where a <see cref="LauncherVersion"/> came from.
/// </summary>
public enum LauncherVersionSource
{
    None,
    PeResource,
    AssemblyMetadata,
    DepsJson,
}

public readonly record struct LauncherVersion(Optional<Version> Version, LauncherVersionSource Source)
{
    public static LauncherVersion Unknown => new(Optional<Version>.None, LauncherVersionSource.None);
}

/// <summary>
/// Reads the version of a game launcher, for .NET launchers whose PE file has no VERSIONINFO resource.
/// </summary>
public interface ILauncherVersionReader
{
    /// <summary>
    /// Tries, in order, the PE version resource of <paramref name="launcher"/>, the metadata of the adjacent
    /// <c>.dll</c> with the same name, and the project entry of the adjacent <c>.deps.json</c>.
    /// </summary>
    LauncherVersion Read(AbsolutePath launcher);
}
