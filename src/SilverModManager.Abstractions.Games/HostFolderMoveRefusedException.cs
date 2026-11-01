using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// A folder was not moved into the game's folder or the trash, and the message says why. Nothing is deleted when
/// this is thrown, and the host folder is still where it was.
/// </summary>
public sealed class HostFolderMoveRefusedException(string message, Exception? cause = null) : UserFacingException(message, cause)
{
    /// <inheritdoc />
    public override string Title => "The folder was not moved";
}
