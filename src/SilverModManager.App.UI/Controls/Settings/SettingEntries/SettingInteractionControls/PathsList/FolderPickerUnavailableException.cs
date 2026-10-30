using SilverModManager.Sdk.Errors;

namespace SilverModManager.App.UI.Controls.Settings.SettingEntries.PathsList;

/// <summary>
/// The player asked for a folder picker from a window that has no storage provider to open one with.
/// </summary>
public sealed class FolderPickerUnavailableException() : UserFacingException("This window cannot open a folder picker")
{
    public override string Title => "Cannot choose a folder";
}
