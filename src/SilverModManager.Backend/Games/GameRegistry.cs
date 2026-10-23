using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Backend.Games;

[UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
internal class GameRegistry : IGameRegistry
{
    private readonly ILogger _logger;
    private readonly IConnection _connection;

    private readonly Lazy<IGameLocator[]> _locators;
    private readonly Lock _lock = new();
    private readonly Lock _moveLock = new();
    private readonly HashSet<(EntityId Row, string Folder)> _loggedAmbiguousMoves = [];
    private readonly Dictionary<EntityId, string> _pendingMoves = [];

    private Optional<ImmutableArray<GameInstallation>> _cachedInstallations = Optional<ImmutableArray<GameInstallation>>.None;

    public GameRegistry(IServiceProvider serviceProvider)
    {
        _logger = serviceProvider.GetRequiredService<ILogger<GameRegistry>>();
        _connection = serviceProvider.GetRequiredService<IConnection>();
        _locators = new Lazy<IGameLocator[]>(() => serviceProvider.GetServices<IGameLocator>().ToArray());
    }

    public ImmutableArray<GameLocatorProblem> Problems { get; private set; } = [];

    public void ClearCache()
    {
        _cachedInstallations = Optional<ImmutableArray<GameInstallation>>.None;
    }

    public ImmutableArray<GameInstallation> LocateGameInstallations()
    {
        if (_cachedInstallations.HasValue) return _cachedInstallations.Value;

        lock (_lock)
        {
            if (_cachedInstallations.HasValue) return _cachedInstallations.Value;

            var results = new List<GameInstallation>();
            var problems = new ProblemSink();

            _logger.LogDebug("Using {Count} game locators", _locators.Value.Length);
            foreach (var locator in _locators.Value)
            {
                try
                {
                    foreach (var locatorResult in locator.Locate(problems))
                    {
                        try
                        {
                            var resolvedLocations = locatorResult.Game.GetLocations(locatorResult.Path.FileSystem, locatorResult);
                            var gameLocations = GameLocations.Create(resolvedLocations);

                            var installation = new GameInstallation(locatorResult, gameLocations);
                            results.Add(installation);

                            if (_logger.IsEnabled(LogLevel.Information))
                            {
                                var locatorIds = installation.LocatorResult.LocatorIds
                                    .Select(x => x.Value)
                                    .Order(StringComparer.OrdinalIgnoreCase)
                                    .Aggregate((a,b) => $"{a}, {b}");

                                _logger.LogInformation("Found game installation for '{Game}' installed using '{Store}' at '{Path}' with locator IDs {LocatorIds}", installation.Game.DisplayName, installation.LocatorResult.Store, installation.LocatorResult.Path, locatorIds);
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, "Exception while locating a game with locator {Locator}", locator.GetType());
                            problems.Report(locator.GetType().Name, $"Failed to resolve the locations of '{locatorResult.Game.DisplayName}' at '{locatorResult.Path}': {e.Message}");
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Exception while locating games with locator {Locator}", locator.GetType());
                    problems.Report(locator.GetType().Name, $"Failed to locate games: {e.Message}");
                }
            }

            var installations = results.ToImmutableArray();
            Problems = problems.Problems;
            _cachedInstallations = installations;

            _logger.LogInformation("Found {Count} game installations", installations.Length);
            return installations;
        }
    }

    public bool TryGetGameInstallation(Loadout.ReadOnly loadout, [NotNullWhen(true)] out GameInstallation? gameInstallation)
    {
        var metadata = loadout.Installation;
        gameInstallation = null;

        // Rows no migration could key (a game this build never knew) have no game to find.
        if (!GameInstallMetadata.Game.TryGetValue(metadata, out var gameId)) return false;

        var candidates = LocateGameInstallations()
            .Where(installation => installation.Game.GameId == gameId && installation.LocatorResult.Store == metadata.Store)
            .ToArray();

        // The row's folder says which install a loadout is on.
        foreach (var candidate in candidates)
        {
            if (!InstallPaths.AreSameFolder(metadata.Path, InstallFolder(candidate))) continue;
            gameInstallation = candidate;
            return true;
        }

        // Otherwise the game moved: the row follows the one install that no row names.
        foreach (var candidate in candidates)
        {
            if (!TryGetMetadata(candidate, out var row) || row.Id != metadata.Id) continue;
            gameInstallation = candidate;
            return true;
        }

        if (candidates.Length > 0)
            _logger.LogWarning("Loadout `{Loadout}` is on `{Path}`, which none of the {Count} installs of {Game} from {Store} is at", loadout.Name, metadata.Path, candidates.Length, candidates[0].Game.DisplayName, metadata.Store);
        return false;
    }

    public bool TryGetMetadata(GameInstallation installation, out GameInstallMetadata.ReadOnly result)
    {
        result = default(GameInstallMetadata.ReadOnly);

        var gameId = installation.Game.GameId;
        var store = installation.LocatorResult.Store;
        var rows = GameInstallMetadata.FindByGame(_connection.Db, gameId)
            .Where(row => row.Store == store)
            .ToArray();

        var folder = InstallFolder(installation);
        foreach (var row in rows)
        {
            if (!InstallPaths.AreSameFolder(row.Path, folder)) continue;
            result = row;
            return true;
        }

        // No row names this folder. Either the install is new, or Steam moved a game between libraries and
        // its row still names the old folder: the loadouts on that row have to follow. That is only certain
        // when one row has lost its install and one install has no row.
        var peers = Peers(installation);
        var staleRows = rows.Where(row => !peers.Any(peer => InstallPaths.AreSameFolder(row.Path, InstallFolder(peer)))).ToArray();
        if (staleRows.Length == 0) return false;

        var unclaimed = peers.Where(peer => !rows.Any(row => InstallPaths.AreSameFolder(row.Path, InstallFolder(peer)))).ToArray();
        if (staleRows.Length == 1 && unclaimed.Length == 1)
        {
            result = FollowMove(staleRows[0], installation);
            return true;
        }

        bool firstTime;
        lock (_moveLock) firstTime = _loggedAmbiguousMoves.Add((staleRows[0].Id, folder.ToString()));
        if (firstTime)
            _logger.LogWarning("{Game} from {Store} at `{Folder}` could be the install of {RowCount} rows whose folders no longer exist; leaving them as they are", installation.Game.DisplayName, store, folder, staleRows.Length);
        return false;
    }

    /// <summary>
    /// The located installs of the same game and store, <paramref name="installation"/> included.
    /// </summary>
    private GameInstallation[] Peers(GameInstallation installation)
    {
        var store = installation.LocatorResult.Store;
        var peers = LocateGameInstallations()
            .Where(other => other.Game.GameId == installation.Game.GameId && other.LocatorResult.Store == store)
            .ToList();
        if (!peers.Any(peer => ReferenceEquals(peer, installation) || peer.Equals(installation))) peers.Add(installation);
        return peers.ToArray();
    }

    /// <summary>
    /// Points the row at the folder the game is in now and keeps the old one, for the note that tells the user.
    /// Returns the row as it is: the write is committed in the background, since callers can be observers on
    /// the database's own thread, which a commit has to wait for. Until it lands the row still names the old
    /// folder, and the pairing above keeps matching it to this install.
    /// </summary>
    private GameInstallMetadata.ReadOnly FollowMove(GameInstallMetadata.ReadOnly row, GameInstallation installation)
    {
        var newPath = InstallFolder(installation).ToString();

        lock (_moveLock)
        {
            if (_pendingMoves.TryGetValue(row.Id, out var pending) && pending == newPath) return row;

            var current = GameInstallMetadata.Load(_connection.Db, row.Id);
            if (current.Path == newPath) return current;

            _pendingMoves[row.Id] = newPath;
            _logger.LogInformation("{Game} moved from `{OldPath}` to `{NewPath}`", installation.Game.DisplayName, current.Path, newPath);
            _ = Task.Run(() => CommitMove(row.Id, current.Path, newPath));
            return current;
        }
    }

    private async Task CommitMove(EntityId row, string oldPath, string newPath)
    {
        try
        {
            using var tx = _connection.BeginTransaction();
            tx.Add(row, GameInstallMetadata.MovedFrom, oldPath);
            tx.Add(row, GameInstallMetadata.Path, newPath);
            await tx.Commit();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not record that the install at `{OldPath}` moved to `{NewPath}`", oldPath, newPath);
        }
        finally
        {
            lock (_moveLock) _pendingMoves.Remove(row);
        }
    }

    /// <summary>
    /// The folder a metadata row records for an install: its game location, as written by the loadout manager.
    /// </summary>
    private static AbsolutePath InstallFolder(GameInstallation installation)
    {
        return installation.Locations.TryGetValue(LocationId.Game, out var descriptor) ? descriptor.Path : installation.LocatorResult.Path;
    }

    private sealed class ProblemSink : IGameLocatorProblemSink
    {
        private readonly List<GameLocatorProblem> _problems = [];

        public ImmutableArray<GameLocatorProblem> Problems => [.. _problems];

        // Locators built once per Wine prefix report the same clash once per prefix.
        public void Report(string locator, string message)
        {
            var problem = new GameLocatorProblem(locator, message);
            if (!_problems.Contains(problem)) _problems.Add(problem);
        }
    }
}
