using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Games;

/// <summary>
/// A file in the game folder could not be read while the folder was indexed, which ends the index: the drive is
/// failing or went away, and carrying on would only report more of the same. The batches already indexed stay.
/// </summary>
public sealed class GameFolderUnreadableException(GameInstallation installation, AbsolutePath file, IOException cause)
    : UserFacingException($"Reading {file} failed: {cause.Message}. Check the drive and try again; nothing was changed.", cause)
{
    /// <inheritdoc />
    public override string Title => $"{installation.Game.DisplayName}'s folder could not be read";
}
