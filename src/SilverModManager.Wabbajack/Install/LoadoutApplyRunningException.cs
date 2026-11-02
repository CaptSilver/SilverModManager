using SilverModManager.Sdk.Errors;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// A modlist install was not started because the loadout is being applied. The apply reads the loadout and writes the
/// game folder from it, so an install writing into it at the same time would be applied half done.
/// </summary>
public sealed class LoadoutApplyRunningException() : UserFacingException("This loadout is being applied. Wait for the apply to finish before installing into it.")
{
    /// <inheritdoc />
    public override string Title => "The install was not started";
}
