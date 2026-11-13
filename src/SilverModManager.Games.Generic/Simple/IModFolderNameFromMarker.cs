namespace SilverModManager.Games.Generic.Simple;

/// <summary>
/// Names a mod's folder from the content of its marker file, for games that list a mod by an id the mod's own
/// manifest holds and so need the folder to carry it.
/// </summary>
public interface IModFolderNameFromMarker
{
    /// <summary>
    /// The folder name <paramref name="markerFile"/> asks for, or null when it asks for none (unreadable, no name, or
    /// a name that is no folder name), which leaves the folder named after the archive.
    /// </summary>
    string? FolderName(byte[] markerFile);

    /// <summary>
    /// True when the game cannot list a mod that gives no name, so the archive is refused instead of falling back to
    /// the archive's folder name.
    /// </summary>
    bool RequireName => false;

    /// <summary>What the player is told when <see cref="RequireName"/> refuses an archive.</summary>
    string RefusalReason => "";
}
