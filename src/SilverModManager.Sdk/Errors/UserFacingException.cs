using JetBrains.Annotations;

namespace SilverModManager.Sdk.Errors;

/// <summary>
/// An exception whose message is written for the player and can be shown as it is: a refusal such as "the game is
/// still running" or "the Proton prefix does not exist yet". Anything that does not derive from this is a bug,
/// and the UI reports it as a crash with the details in the log only.
/// </summary>
[PublicAPI]
public abstract class UserFacingException : Exception
{
    /// <summary>
    /// Creates the exception with a message for the player.
    /// </summary>
    protected UserFacingException(string message) : base(message) { }

    /// <summary>
    /// Creates the exception with a message for the player and the failure behind it.
    /// </summary>
    protected UserFacingException(string message, Exception? innerException) : base(message, innerException) { }

    /// <summary>
    /// The heading of the dialog that shows this refusal, so each kind says what was not done.
    /// </summary>
    public abstract string Title { get; }

    /// <summary>
    /// A follow-up the dialog offers next to OK, stamped by whoever knows the context the refusal came from.
    /// </summary>
    public UserFacingAction? Action { get; set; }
}
