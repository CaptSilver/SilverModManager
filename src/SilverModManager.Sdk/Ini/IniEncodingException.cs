using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Sdk.Ini;

/// <summary>
/// Thrown when an INI file cannot be written in the encoding it was read in, for example a character outside
/// Latin-1 set in a file the game stores as Windows-1252. The file is left as it was; writing a question mark
/// instead would corrupt the setting without telling anyone.
/// </summary>
[PublicAPI]
public class IniEncodingException(string settingName, IniEncoding encoding)
    : UserFacingException(
        $"The setting \"{settingName}\" has a character that the INI file's {encoding} encoding cannot hold, so the file was not written. " +
        "Use only plain characters in this setting, or change the file's encoding with a text editor first."
    )
{
    /// <inheritdoc />
    public override string Title => "An INI file was not changed";
}
