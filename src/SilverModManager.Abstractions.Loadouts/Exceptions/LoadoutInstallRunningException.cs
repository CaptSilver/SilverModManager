using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Loadouts.Exceptions;

/// <summary>Thrown when a loadout is applied while an install is still writing into it.</summary>
public class LoadoutInstallRunningException()
    : UserFacingException("An install into this loadout is still running. Wait for it to finish before applying.")
{
    /// <inheritdoc />
    public override string Title => "The install is still running";
}
