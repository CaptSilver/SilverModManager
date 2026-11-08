namespace SilverModManager.App.UI.Pages.Collections.Update;

/// <summary>
/// A newer version of an installed collection, whatever it came from. The page's Update item and the header pill hold
/// one; the generic update flow drives it from the review to the toast. Nothing here knows Nexus or
/// Wabbajack types: each source answers with its own implementation.
/// </summary>
public interface ICollectionUpdate
{
    string Name { get; }
    string CurrentVersionText { get; }
    string NewVersionText { get; }

    /// <summary>A line the review shows under the header, such as "re-uploaded by the author". Null when there is nothing to add.</summary>
    string? Note { get; }

    /// <summary>What the update would do, without doing any of it.</summary>
    Task<CollectionUpdateReview> Review(IProgress<string>? progress, CancellationToken ct);

    /// <summary>Whether everything the update needs is already in the library.</summary>
    Task<bool> IsDownloaded(CancellationToken ct);

    /// <summary>
    /// Gets what the library lacks. The implementation owns the questions that come first (Nexus login and premium).
    /// </summary>
    Task<CollectionUpdateDownload> Download(CancellationToken ct);

    /// <summary>Moves the installed collection to the new version. <paramref name="skipMissing"/> leaves out files whose archive is missing.</summary>
    Task<CollectionUpdateOutcome> Apply(bool skipMissing, CancellationToken ct);
}

public enum CollectionUpdateDownload
{
    Downloaded,

    /// <summary>The implementation could not start downloading (not logged in, no premium) and already said why.</summary>
    CantDownload,

    /// <summary>Some of what the update needs did not arrive, so applying it would install only part.</summary>
    NotAllDownloaded,
}
