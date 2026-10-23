using JetBrains.Annotations;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Games;

/// <summary>
/// Finds installed Proton builds and picks the one Steam would use for a game.
/// </summary>
[PublicAPI]
public interface IProtonResolver
{
    /// <summary>
    /// Every Proton build visible to the given Steam installation, oldest first.
    /// </summary>
    IReadOnlyList<ProtonBuild> ListBuilds(AbsolutePath steamInstallPath);

    /// <summary>
    /// The build Steam runs the app with: the one mapped to the app in <c>config.vdf</c>, else the default
    /// mapping, else the newest stable Valve build. Never a guess: when the mapped build is not installed
    /// or nothing is installed, the result carries a diagnostic instead.
    /// </summary>
    ProtonResolution ResolveForApp(AbsolutePath steamInstallPath, uint appId);
}
