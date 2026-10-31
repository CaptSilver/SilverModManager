using SilverModManager.Sdk.Errors;

namespace SilverModManager.SingleProcess.Exceptions;

/// <summary>
/// Thrown when the running SilverModManager accepts the connection but says nothing within the greeting timeout. Unlike
/// <see cref="VersionMismatch"/> it gives no sign of being alive, but a busy main looks the same, so it is never closed for it.
/// </summary>
public class MainProcessSilent(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "SilverModManager did not answer";
}
