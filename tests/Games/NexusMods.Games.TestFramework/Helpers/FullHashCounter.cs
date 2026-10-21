using Microsoft.Extensions.DependencyInjection;
using NexusMods.Backend.Games;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.Paths;

namespace NexusMods.Games.TestFramework.Helpers;

/// <summary>
/// Counts the files the game indexer had to read whole to hash. A file it can vouch for from stored hash rows or
/// from the previous index is never read, so a count above zero after an unchanged re-index is a folder read twice.
/// </summary>
public sealed class FullHashCounter
{
    private readonly List<AbsolutePath> _paths = [];

    public int Count
    {
        get { lock (_paths) return _paths.Count; }
    }

    public IReadOnlyList<AbsolutePath> Paths
    {
        get { lock (_paths) return _paths.ToArray(); }
    }

    public void Reset()
    {
        lock (_paths) _paths.Clear();
    }

    internal void Add(AbsolutePath path)
    {
        lock (_paths) _paths.Add(path);
    }
}

public static class FullHashCounterExtensions
{
    /// <summary>Counts every whole-file hash the game indexer takes into <paramref name="counter"/>.</summary>
    public static IServiceCollection AddFullHashCounter(this IServiceCollection services, FullHashCounter counter)
        => services.AddSingleton<IFullFileHasher>(new CountingFullFileHasher(counter));

    private sealed class CountingFullFileHasher(FullHashCounter counter) : IFullFileHasher
    {
        public async ValueTask<Hash> Hash(AbsolutePath file, CancellationToken cancellationToken)
        {
            counter.Add(file);
            return await file.XxHash3Async(token: cancellationToken);
        }
    }
}
