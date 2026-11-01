namespace SilverModManager.Abstractions.Games;

/// <summary>
/// Where a folder goes when the app removes it for the user: somewhere they can still get it back from.
/// </summary>
public interface ITrash
{
    /// <summary>
    /// Moves the folder at <paramref name="path"/> into the trash, never deleting it.
    /// </summary>
    /// <exception cref="HostFolderMoveRefusedException">
    /// The folder could not be moved, for instance because it is on another file system than the trash. It is
    /// left where it was.
    /// </exception>
    void MoveToTrash(string path);
}
