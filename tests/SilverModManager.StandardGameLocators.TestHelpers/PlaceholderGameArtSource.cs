using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;

namespace NexusMods.StandardGameLocators.TestHelpers;

/// <summary>
/// Cover art for a test host: the same picture for every game, from the test helpers' own resources. The
/// app's source reads Steam's <c>appcache/librarycache</c> through GameFinder's search for the real Steam
/// folder, so a test that renders a game tile would read the developer's cache.
/// </summary>
public sealed class PlaceholderGameArtSource : IGameArtSource
{
    private static readonly EmbeddedResourceStreamFactory<StubbedGame> Picture =
        new("NexusMods.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");

    public IStreamFactory Tile(ImmutableArray<uint> steamAppIds) => Picture;

    public IStreamFactory Icon(ImmutableArray<uint> steamAppIds) => Picture;
}

public static class PlaceholderGameArtSourceExtensions
{
    /// <summary>
    /// Replaces whatever <see cref="IGameArtSource"/> is registered with a <see cref="PlaceholderGameArtSource"/>.
    /// </summary>
    public static IServiceCollection ReplaceGameArtSource(this IServiceCollection services)
    {
        var registered = services.Where(d => d.ServiceType == typeof(IGameArtSource)).ToArray();
        if (registered.Length == 0)
            throw new InvalidOperationException("No IGameArtSource is registered; if it moved, update this helper");

        foreach (var descriptor in registered) services.Remove(descriptor);
        return services.AddSingleton<IGameArtSource, PlaceholderGameArtSource>();
    }
}
