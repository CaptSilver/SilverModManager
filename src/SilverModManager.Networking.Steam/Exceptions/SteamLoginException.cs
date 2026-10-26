using SilverModManager.Sdk.Errors;

namespace SilverModManager.Networking.Steam.Exceptions;

/// <summary>
/// The Steam login could not be started or was rejected.
/// </summary>
public class SteamLoginException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "Steam sign-in failed";
}
