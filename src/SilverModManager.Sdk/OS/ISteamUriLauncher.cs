using JetBrains.Annotations;

namespace SilverModManager.Sdk;

/// <summary>
/// The one way the app hands a <c>steam://</c> URL to Steam. Going through the OS URL handler depends on the
/// session having one registered, which a gamescope session does not.
/// </summary>
[PublicAPI]
public interface ISteamUriLauncher
{
    /// <summary>
    /// Asks the running Steam client to act on the URL, without waiting for it to finish.
    /// </summary>
    /// <exception cref="ArgumentException">The URL's scheme is not <c>steam</c>.</exception>
    void Open(Uri steamUri);
}
