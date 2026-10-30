using SilverModManager.Sdk.Errors;

namespace SilverModManager.App.UI.Pages.LibraryPage;

/// <summary>
/// The player asked to add files from disk in a window that has no storage provider to open a picker with.
/// </summary>
public sealed class FilePickerUnavailableException() : UserFacingException("This window cannot open a file picker")
{
    public override string Title => "Cannot choose files";
}
