using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Networking.Steam.Workshop;

/// <summary>
/// Steam's Web API did not answer, or answered with an error status.
/// </summary>
[PublicAPI]
public sealed class SteamWorkshopUnavailableException(string message, Exception? inner = null) : UserFacingException(message, inner)
{
    /// <inheritdoc />
    public override string Title => "Steam is not answering requests right now";
}

/// <summary>
/// Steam answered, but not with the item details the client asked for.
/// </summary>
[PublicAPI]
public sealed class SteamWorkshopReplyUnreadableException(Exception? inner = null)
    : UserFacingException("Steam sent an answer about Workshop items that the app could not read. Try again later.", inner)
{
    /// <inheritdoc />
    public override string Title => "Steam's answer was not understood";
}
