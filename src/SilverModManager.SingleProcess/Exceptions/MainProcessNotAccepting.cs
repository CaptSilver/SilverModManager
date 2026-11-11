using SilverModManager.Sdk.Errors;

namespace SilverModManager.SingleProcess.Exceptions;

/// <summary>
/// Thrown when the running SilverModManager's port does not take the connection within the connect timeout. Kept apart
/// from <see cref="MainProcessSilent"/> (connected, then no greeting) and from a verb that runs long, so each is told
/// to the caller as what it is.
/// </summary>
public class MainProcessNotAccepting(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "SilverModManager did not answer";
}
