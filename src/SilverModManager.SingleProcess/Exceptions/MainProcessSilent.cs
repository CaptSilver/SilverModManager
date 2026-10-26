using SilverModManager.Sdk.Errors;

namespace SilverModManager.SingleProcess.Exceptions;

/// <summary>
/// Thrown when the running SilverModManager accepts the connection but says nothing within the greeting timeout. Unlike
/// <see cref="VersionMismatch"/> it gives no sign of being alive, so startup may treat it as hung.
/// </summary>
public class MainProcessSilent(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "SilverModManager did not answer";
}
