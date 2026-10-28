using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Games.FileHashes;
using NexusMods.Hashing.xxHash3;
using NexusMods.Hashing.xxHash3.Paths;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Games.TestFramework.Helpers;

/// <summary>
/// Counts the files the game indexer and the synchronizer had to read whole to hash. A file it can vouch for from stored hash rows or
/// from the previous index is never read, so a count above zero after an unchanged re-index is a folder read twice.
/// </summary>
public sealed class FullHashCounter
{
    private readonly List<AbsolutePath> _paths = [];
    private readonly List<AbsolutePath> _baselineOpens = [];

    public int Count
    {
        get { lock (_paths) return _paths.Count; }
    }

    public IReadOnlyList<AbsolutePath> Paths
    {
        get { lock (_paths) return _paths.ToArray(); }
    }

    /// <summary>The files the local baseline build opened to hash them.</summary>
    public IReadOnlyList<AbsolutePath> BaselineOpens
    {
        get { lock (_baselineOpens) return _baselineOpens.ToArray(); }
    }

    public void Reset()
    {
        lock (_paths) _paths.Clear();
        lock (_baselineOpens) _baselineOpens.Clear();
    }

    internal void AddBaselineOpen(AbsolutePath path)
    {
        lock (_baselineOpens) _baselineOpens.Add(path);
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
        => services
            .AddSingleton<IFullFileHasher>(new CountingFullFileHasher(counter))
            .AddSingleton<IBaselineFileOpener>(new CountingBaselineFileOpener(counter));

    private sealed class CountingBaselineFileOpener(FullHashCounter counter) : IBaselineFileOpener
    {
        public Stream OpenRead(AbsolutePath path)
        {
            counter.AddBaselineOpen(path);
            return path.Read();
        }
    }

    private sealed class CountingFullFileHasher(FullHashCounter counter) : IFullFileHasher
    {
        public async ValueTask<Hash> Hash(AbsolutePath file, CancellationToken cancellationToken)
        {
            counter.Add(file);
            return await file.XxHash3Async(token: cancellationToken);
        }

        public async ValueTask<MultiHash> HashAll(AbsolutePath file, CancellationToken cancellationToken)
        {
            counter.Add(file);
            await using var stream = file.Read();
            return await MultiHasher.HashStream(stream, cancellationToken);
        }
    }
}
