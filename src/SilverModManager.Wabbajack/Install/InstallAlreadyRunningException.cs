using SilverModManager.Sdk.Errors;

namespace SilverModManager.Wabbajack.Install;

/// <summary>
/// A modlist install was not started because another one is already writing into the same loadout. Two installs
/// into one loadout would write the same group at once.
/// </summary>
public sealed class InstallAlreadyRunningException() : UserFacingException("An install into this loadout is already running")
{
    /// <inheritdoc />
    public override string Title => "The install was not started";
}
