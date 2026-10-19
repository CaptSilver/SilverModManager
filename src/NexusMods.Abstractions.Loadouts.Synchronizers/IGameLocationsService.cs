using System.Collections.Frozen;
using JetBrains.Annotations;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

[PublicAPI]
public record struct IndexFileResult(
    GamePath Path,
    Hash Hash,
    Size Size,
    DateTimeOffset LastModified
);

[PublicAPI]
public record IndexGameResult(
    FrozenDictionary<GamePath, IndexFileResult> NewFiles,
    FrozenDictionary<GamePath, IndexFileResult> ModifiedFiles,
    FrozenSet<GamePath> RemovedFiles
);

/// <summary>
/// Service responsible for handling operations on game locations/installations.
/// </summary>
[PublicAPI]
public interface IGameLocationsService
{
    Task<IndexGameResult> IndexGame(
        GameInstallation installation,
        FrozenDictionary<GamePath, DiskStateEntry.ReadOnly> previousDiskState,
        IGamePathFilter filter,
        CancellationToken cancellationToken = default
    );

    // Task RemoveEmptyDirectories(GameInstallation installation, CancellationToken cancellationToken = default);
}

[PublicAPI]
public interface IGamePathFilter
{
    bool ShouldFilter(GamePath gamePath);
}

public static class GamePathFilters
{
    public static readonly IGamePathFilter Empty = new EmptyFilter();

    public static IGamePathFilter Create(Func<GamePath, bool> predicate)
    {
        return new PredicateFilter(predicate);
    }

    /// <summary>
    /// A filter that rejects a path when any of the given filters does.
    /// </summary>
    public static IGamePathFilter Any(params IGamePathFilter[] filters)
    {
        return new AnyFilter(filters);
    }

    private class AnyFilter(IGamePathFilter[] filters) : IGamePathFilter
    {
        public bool ShouldFilter(GamePath gamePath)
        {
            foreach (var filter in filters)
            {
                if (filter.ShouldFilter(gamePath)) return true;
            }

            return false;
        }
    }

    private class PredicateFilter : IGamePathFilter
    {
        private readonly Func<GamePath, bool> _predicate;

        public PredicateFilter(Func<GamePath, bool> predicate)
        {
            _predicate = predicate;
        }

        public bool ShouldFilter(GamePath gamePath) => _predicate(gamePath);
    }

    private class EmptyFilter : IGamePathFilter
    {
        public bool ShouldFilter(GamePath gamePath) => false;
    }
}
