using JetBrains.Annotations;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;

/// <summary>
/// Thrown when a change to a loadout's INI tweak is refused: a name or value that cannot be written to an INI, a
/// tweak that changed since the caller read it, or a tweak layer whose bytes are gone. Nothing was written.
/// </summary>
[PublicAPI]
public class IniTweakRefusedException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The INI setting was not changed";
}
