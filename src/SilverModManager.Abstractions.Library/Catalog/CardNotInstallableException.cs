using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Abstractions.Library.Catalog;

/// <summary>
/// A card the source lists but gives the app nothing to install, such as a package whose versions are all withdrawn.
/// </summary>
public sealed class CardNotInstallableException(string title)
    : UserFacingException($"{MarkdownText.Code(title)} has no version this app can install.")
{
    /// <inheritdoc />
    public override string Title => "Nothing was installed";
}
