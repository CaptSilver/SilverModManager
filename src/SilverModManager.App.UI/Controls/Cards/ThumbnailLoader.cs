using Avalonia.Media;
using Avalonia.Media.Imaging;
using BitFaster.Caching.Lru;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Resources;

namespace SilverModManager.App.UI.Controls.Cards;

/// <summary>
/// <see cref="IThumbnailLoader"/> over an image pipeline, with a bounded in-memory cache of decoded images keyed by URL.
/// </summary>
public sealed class ThumbnailLoader : IThumbnailLoader
{
    /// <summary>Roughly two screens of a long scroll back and forth, at up to about 330 KB an image.</summary>
    public const int DefaultCapacity = 256;

    private readonly IResourceLoader<Uri, Bitmap> _pipeline;
    // The cache holds images through a plain wrapper. BitFaster disposes any IDisposable value it evicts or replaces,
    // and a Bitmap pushed out of the cache is usually still on screen in a card: drawing a disposed Bitmap throws a
    // NullReferenceException from inside the renderer. Without the wrapper an evicted image is left to the GC.
    private readonly ConcurrentLru<Uri, CachedImage> _cache;
    private readonly ILogger? _logger;

    public ThumbnailLoader(IResourceLoader<Uri, Bitmap> pipeline, IImage placeholder, int capacity = DefaultCapacity, ILogger? logger = null)
    {
        _pipeline = pipeline;
        _cache = new ConcurrentLru<Uri, CachedImage>(capacity);
        _logger = logger;
        Placeholder = placeholder;
    }

    public IImage Placeholder { get; }

    public IImage? TryGetCached(Uri url) => _cache.TryGet(url, out var entry) ? entry.Image : null;

    public async Task<IImage> LoadAsync(Uri url, CancellationToken cancellationToken)
    {
        // The pipeline's HTTP loader only speaks https; anything else in a catalog entry is not worth a request.
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps) return Placeholder;
        if (_cache.TryGet(url, out var cached)) return cached.Image;

        try
        {
            // Off the caller's (UI) thread: the pipeline decodes and resizes on whichever thread it is awaited from.
            var resource = await Task.Run(async () => await _pipeline.LoadResourceAsync(url, cancellationToken), cancellationToken);
            // Two cards can load one URL at once; the second to finish takes the first one's image so both show the same.
            return _cache.GetOrAdd(url, _ => new CachedImage(resource.Data)).Image;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger?.LogDebug(e, "Could not load the thumbnail {Url}", url);
            return Placeholder;
        }
    }

    private sealed record CachedImage(IImage Image);
}
