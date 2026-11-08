using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Loadouts.Exceptions;

/// <summary>Thrown when a loadout is applied while an install is still writing into it, or an update waits to be finished.</summary>
public class LoadoutInstallRunningException(string message, string title) : UserFacingException(message)
{
    /// <summary>The refusal for an install that is still running.</summary>
    public LoadoutInstallRunningException()
        : this("An install into this loadout is still running. Wait for it to finish before applying.", "The install is still running")
    {
    }

    /// <inheritdoc />
    public override string Title => title;
}
