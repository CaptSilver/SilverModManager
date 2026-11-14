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

/// <summary>
/// A Steam verb refused its input; the message says what to change.
/// </summary>
public sealed class SteamVerbRefusedException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The command was not run";
}
