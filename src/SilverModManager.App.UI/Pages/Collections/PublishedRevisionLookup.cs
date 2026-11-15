using DynamicData.Kernel;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Networking.NexusWebApi.Errors;

namespace SilverModManager.App.UI.Pages.Collections;

internal static class PublishedRevisionLookup
{
    /// <summary>
    /// Reads the latest published revision out of a lookup result. A collection that was deleted or hidden, or a query
    /// that came back with errors, has no data: that is logged and reported as unavailable so callers keep whatever
    /// update state they already have instead of throwing inside a subscriber.
    /// </summary>
    internal static bool TryReadPublishedRevision(
        this GraphQlResult<Optional<RevisionNumber>, NotFound> result,
        ILogger logger,
        CollectionSlug collectionSlug,
        out Optional<RevisionNumber> published)
    {
        if (result.TryGetData(out published)) return true;

        logger.LogWarning("Could not look up the latest published revision of collection `{Slug}`: {Errors}",
            collectionSlug, string.Join("; ", result.Errors.Select(error => $"{error.Key}: {error.Value.Message}")));
        published = Optional<RevisionNumber>.None;
        return false;
    }
}
