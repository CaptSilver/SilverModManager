using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Library.Steam;

/// <summary>
/// A file fetched from a Steam depot did not have the SHA-1 it was expected to have. Nothing was added to the library.
/// </summary>
[PublicAPI]
public sealed class SteamDepotIntegrityException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The Steam file was rejected";
}

/// <summary>
/// The manifest does not list the file that was asked for, so there was nothing to fetch.
/// </summary>
[PublicAPI]
public sealed class SteamDepotFileNotFoundException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The file is not in that Steam manifest";
}
