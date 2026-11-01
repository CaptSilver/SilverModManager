using SilverModManager.Sdk.Errors;

namespace SilverModManager.Platform.Proton.Tools;

/// <summary>A tool or pipeline cannot be added or started, for a reason the message states.</summary>
public sealed class ToolRefusedException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The tool was not run";
}
