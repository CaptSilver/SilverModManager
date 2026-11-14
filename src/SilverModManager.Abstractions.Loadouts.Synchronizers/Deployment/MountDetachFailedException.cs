using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;

/// <summary>
/// A mount left by an earlier run could not be detached, so what is staged under it was not touched.
/// </summary>
public sealed class MountDetachFailedException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "A mounted game folder could not be released";

    /// <param name="mountPoint">The folder the stale mount covers.</param>
    /// <param name="reason">What the unmounter said.</param>
    public static MountDetachFailedException For(string mountPoint, string reason) =>
        new($"{mountPoint} is still mounted from an earlier run and could not be detached ({reason}). Run 'fusermount3 -uz {mountPoint}' and then silvermodmanager doctor");
}
