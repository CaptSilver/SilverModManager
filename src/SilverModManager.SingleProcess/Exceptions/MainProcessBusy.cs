using SilverModManager.Sdk.Errors;

namespace SilverModManager.SingleProcess.Exceptions;

/// <summary>
/// Thrown when SilverModManager is running but does not answer. A slow main is not a dead one, so the caller is asked
/// to try again and nothing is closed.
/// </summary>
public class MainProcessBusy() : UserFacingException("SilverModManager is running but isn't answering right now (it may be busy). Try again in a moment.")
{
    /// <inheritdoc />
    public override string Title => "SilverModManager is busy";
}
