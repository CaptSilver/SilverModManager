using SilverModManager.Sdk.Errors;

namespace SilverModManager.App.UI.Pages.Collections.Update;

/// <summary>
/// An update's target revision isn't in the library and couldn't be fetched, so there is nothing to review or apply.
/// </summary>
public sealed class CollectionRevisionUnavailableException(string collectionName, string version) : UserFacingException(
    $"{MarkdownText.Code(version)} of {MarkdownText.Code(collectionName)} isn't in your library, and it can't be downloaded without a Nexus Mods login and Premium.")
{
    public override string Title => "Couldn't get the new revision";
}
