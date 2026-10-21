using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Backend.Games;

[UsedImplicitly(ImplicitUseKindFlags.InstantiatedNoFixedConstructorSignature)]
internal class GameRegistry : IGameRegistry
{
    private readonly ILogger _logger;
    private readonly IConnection _connection;

    private readonly Lazy<IGameLocator[]> _locators;
    private readonly Lock _lock = new();

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

        switch (candidates.Length)
        {
            case 0:
                return false;
            case 1:
                gameInstallation = candidates[0];
                return true;
        }

        // Several installs of one game from one store: the stored folder says which one the loadout is on.
        foreach (var candidate in candidates)
        {
            if (!InstallPaths.AreSameFolder(metadata.Path, InstallFolder(candidate))) continue;
            gameInstallation = candidate;
            return true;
        }

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

        // A single row is the install even if its folder changed: Steam moves games between libraries and the
        // loadouts on the row have to follow. It is only another install's row when that other install is
        // located right now, at the folder the row names.
        var ownRows = rows.Where(row => !IsClaimedByAnotherInstall(row, installation)).ToArray();

        if (ownRows.Length == 1)
        {
            result = ownRows[0];
            return true;
        }

        foreach (var row in ownRows)
        {
            if (!InstallPaths.AreSameFolder(row.Path, InstallFolder(installation))) continue;
            result = row;
            return true;
        }

        return false;
    }

    private bool IsClaimedByAnotherInstall(GameInstallMetadata.ReadOnly row, GameInstallation installation)
    {
        var store = installation.LocatorResult.Store;
        var folder = InstallFolder(installation);

        foreach (var other in LocateGameInstallations())
        {
            if (ReferenceEquals(other, installation) || other.Equals(installation)) continue;
            if (other.Game.GameId != installation.Game.GameId || other.LocatorResult.Store != store) continue;
            if (InstallPaths.AreSameFolder(row.Path, InstallFolder(other)) && !InstallPaths.AreSameFolder(row.Path, folder)) return true;
        }

        return false;
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
