using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Loadouts.Exceptions;

/// <summary>
/// Exception thrown when an executable is in use
/// </summary>
public class ExecutableInUseException : UserFacingException
{
    /// <inheritdoc />
    public ExecutableInUseException(string message) : base(message) { }
    /// <inheritdoc />
    public ExecutableInUseException(string message, Exception? innerException) : base(message, innerException) { }

    /// <inheritdoc />
    public override string Title => "The game is still running";
}
