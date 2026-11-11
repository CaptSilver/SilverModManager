using SilverModManager.Sdk.Errors;

namespace SilverModManager.Platform.Proton.Tools;

/// <summary>A tool or pipeline cannot be added or started, for a reason the message states.</summary>
public sealed class ToolRefusedException : UserFacingException
{
    /// <summary>Creates the refusal with a message for the player.</summary>
    public ToolRefusedException(string message) : base(message) { }

    /// <summary>Creates the refusal with a message for the player and the refusal it wraps.</summary>
    public ToolRefusedException(string message, Exception? inner) : base(message, inner) { }

    /// <inheritdoc />
    public override string Title => "The tool was not run";
}
