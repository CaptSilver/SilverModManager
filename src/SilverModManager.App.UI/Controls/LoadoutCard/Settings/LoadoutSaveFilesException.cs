using SilverModManager.Sdk.Errors;

namespace SilverModManager.App.UI.Controls.LoadoutCard;

/// <summary>
/// Copying or deleting a loadout's save files was refused or failed on a file error. The message names the file and
/// is written for the player; the dialog title says which of the two did not happen.
/// </summary>
public sealed class LoadoutSaveFilesException(string title, string message, Exception? cause = null) : UserFacingException(message, cause)
{
    /// <inheritdoc />
    public override string Title { get; } = title;
}
