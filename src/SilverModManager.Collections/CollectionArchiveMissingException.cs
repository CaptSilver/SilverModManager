using SilverModManager.Sdk.Errors;

namespace SilverModManager.Collections;

/// <summary>A collection mod cannot be installed because its archive is no longer in the library.</summary>
public sealed class CollectionArchiveMissingException(string modName) : UserFacingException($"{MarkdownText.Code(modName)} is no longer in the library. Download it again from the collection page.")
{
    /// <inheritdoc />
    public override string Title => "Download it again";
}
