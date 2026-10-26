using SilverModManager.Sdk.Errors;

namespace SilverModManager.SingleProcess.Exceptions;

/// <summary>
/// Thrown when the running SilverModManager is not the build that is trying to send it a command, or does not
/// say which build it is.
/// </summary>
public class VersionMismatch(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "SilverModManager versions differ";
}
