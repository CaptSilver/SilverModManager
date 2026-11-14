using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Library.Catalog;

/// <summary>
/// What installing a card did.
/// </summary>
public abstract record CatalogInstallOutcome
{
    private CatalogInstallOutcome() { }

    /// <summary>
    /// The mod, and anything it needs, is in the loadout.
    /// </summary>
    public sealed record Installed(IReadOnlyList<InstallOutcome> PerPackage) : CatalogInstallOutcome;

    /// <summary>
    /// The game or Steam syncs the mod, so nothing was downloaded; <paramref name="Note"/> says what happens next.
    /// </summary>
    public sealed record Subscribed(string Note) : CatalogInstallOutcome;

    /// <summary>
    /// The source only lets its own site hand out the download. Nothing has been opened yet: the page opens
    /// <paramref name="Page"/> in the browser and the verbs print it.
    /// </summary>
    public sealed record OpenedOnWebsite(Uri Page) : CatalogInstallOutcome;

    /// <summary>
    /// The install was not done on purpose; <paramref name="Why"/> is what the user is told.
    /// </summary>
    public sealed record Refused(UserFacingException Why) : CatalogInstallOutcome;
}
