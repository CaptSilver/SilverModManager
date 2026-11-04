using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Backend.Games;

/// <summary>
/// The app's <see cref="IGameArtSource"/>: asks each <see cref="IGameArtProvider"/> in order and uses the
/// first answer. The last link is meant to always answer, so a game never ends up with no picture.
/// </summary>
internal sealed class GameArtChain(IEnumerable<IGameArtProvider> providers, ILogger<GameArtChain> logger) : IGameArtSource
{
    private readonly IGameArtProvider[] _providers = providers.ToArray();

    public IStreamFactory Tile(GameArtRequest request) => new LazyStreamFactory(() => Resolve(request, "tile", (p, r) => p.FindTileAsync(r)));

    public IStreamFactory Icon(GameArtRequest request) => new LazyStreamFactory(() => Resolve(request, "icon", (p, r) => p.FindIconAsync(r)));

    private async ValueTask<Stream> Resolve(GameArtRequest request, string slot, Func<IGameArtProvider, GameArtRequest, ValueTask<IStreamFactory?>> find)
    {
        foreach (var provider in _providers)
        {
            try
            {
                var found = await find(provider, request).ConfigureAwait(false);
                if (found != null) return await found.GetStreamAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One broken source (unreadable cache folder, failed download, a found file that won't open) must not blank the picture.
                logger.LogDebug(exception, "{Provider} could not supply the {Slot} for {Game}", provider.GetType().Name, slot, request.DisplayName);
            }
        }

        throw new InvalidOperationException($"No art source answered for the {slot} of {request.DisplayName}; the last source in the chain must always answer");
    }

    /// <summary>
    /// Defers the lookup to each read: the game classes build their factories in a
    /// constructor, which should not touch the disk or network, and art may show up later.
    /// </summary>
    private sealed class LazyStreamFactory(Func<ValueTask<Stream>> resolve) : IStreamFactory
    {
        public NexusMods.Paths.RelativePath FileName => NexusMods.Paths.RelativePath.Empty;

        public ValueTask<Stream> GetStreamAsync() => resolve();
    }
}
