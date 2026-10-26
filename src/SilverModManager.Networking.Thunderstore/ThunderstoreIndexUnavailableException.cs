using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Networking.Thunderstore;

/// <summary>
/// A community's package index has never been downloaded, or what is on disk cannot be read. Refresh it first.
/// </summary>
[PublicAPI]
public sealed class ThunderstoreIndexUnavailableException : UserFacingException
{
    public ThunderstoreIndexUnavailableException(string community, Exception? inner = null)
        : base($"No usable Thunderstore package index is cached for community '{community}'. Refresh the index and try again.", inner)
    {
        Community = community;
    }

    /// <inheritdoc />
    public override string Title => "The Thunderstore index is not available";

    public string Community { get; }
}
