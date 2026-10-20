using JetBrains.Annotations;

namespace NexusMods.Sdk.Games;

/// <summary>
/// Finds the Wine prefix a located game runs in.
/// </summary>
[PublicAPI]
public interface IPrefixResolver
{
    /// <summary>
    /// The prefix for the installation. Steam's own prefix wins when the installation has one, otherwise the
    /// layer the locator recorded; two different prefixes for one installation is reported, not picked
    /// between. Never guesses from modification times.
    /// </summary>
    PrefixResolution Resolve(GameInstallation installation);
}
