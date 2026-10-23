using JetBrains.Annotations;

namespace SilverModManager.Networking.Thunderstore;

/// <summary>
/// A community's package index has never been downloaded, or what is on disk cannot be read. Refresh it first.
/// </summary>
[PublicAPI]
public sealed class ThunderstoreIndexUnavailableException : Exception
{
    public ThunderstoreIndexUnavailableException(string community, Exception? inner = null)
        : base($"No usable Thunderstore package index is cached for community '{community}'", inner)
    {
        Community = community;
    }

    public string Community { get; }
}
