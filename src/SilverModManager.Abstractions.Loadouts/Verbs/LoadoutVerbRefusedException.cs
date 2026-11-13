using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Loadouts.Verbs;

/// <summary>A verb refused its input; the message says what to change and the exit code is 1.</summary>
public sealed class LoadoutVerbRefusedException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The command was not run";
}
