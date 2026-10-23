using JetBrains.Annotations;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// An installed Proton build.
/// </summary>
/// <param name="Name">
/// The name Steam uses for the build in <c>CompatToolMapping</c>: the internal name from
/// <c>compatibilitytool.vdf</c> for custom builds, <c>proton_9</c>-style names for Valve's own.
/// </param>
/// <param name="Directory">The directory that holds the <c>proton</c> script.</param>
[PublicAPI]
public sealed record ProtonBuild(string Name, AbsolutePath Directory)
{
    /// <summary>
    /// The <c>proton</c> script that runs a program inside a prefix.
    /// </summary>
    public AbsolutePath Launcher => Directory.Combine("proton");
}
