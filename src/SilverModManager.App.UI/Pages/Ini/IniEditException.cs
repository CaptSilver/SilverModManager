using SilverModManager.Sdk.Errors;

namespace SilverModManager.App.UI.Pages.Ini;

/// <summary>
/// An edit on the INI page was refused, for example a value of the wrong type or a file that is not in the game's
/// folder. The message names the setting or file; the title says which edit did not happen.
/// </summary>
public sealed class IniEditException(string title, string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title { get; } = title;
}
