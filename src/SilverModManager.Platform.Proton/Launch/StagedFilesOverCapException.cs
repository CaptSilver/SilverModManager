using SilverModManager.Sdk.Errors;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// The files a game wrote through a mounted folder add up to more than the promotion size cap, so none were promoted.
/// </summary>
public sealed class StagedFilesOverCapException : UserFacingException
{
    /// <summary>Creates the refusal with a message for the player.</summary>
    public StagedFilesOverCapException(string message) : base(message) { }

    /// <inheritdoc />
    public override string Title => "The files were not promoted";
}
