using System.Collections.Immutable;
using System.Runtime.InteropServices;
using GameFinder.Launcher.Heroic;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Games.Locators;

internal class HeroicGOGLocator : IGameLocator
{
    private readonly ILogger _logger;
    private readonly IFileSystem _fileSystem;
    private readonly HeroicGOGHandler _handler;
    private readonly StoreIdMap<long> _registeredGames;

    private static readonly GameStore Store = GameStore.GOG;

    public HeroicGOGLocator(IEnumerable<IGameData> games, ILoggerFactory loggerFactory, IFileSystem fileSystem)
    {
        _logger = loggerFactory.CreateLogger<HeroicGOGLocator>();
        _fileSystem = fileSystem;

        _handler = new HeroicGOGHandler(
            fileSystem: fileSystem,
            logger: loggerFactory.CreateLogger<HeroicGOGHandler>()
        );

        _registeredGames = StoreIdMap.Build(games, game => game.StoreIdentifiers.GOGProductIds, "GOG product", nameof(HeroicGOGLocator), _logger);
    }

    public IEnumerable<GameLocatorResult> Locate() => LocateGames(null);

    public IEnumerable<GameLocatorResult> Locate(IGameLocatorProblemSink problems)
    {
        _registeredGames.ReportTo(problems);
        return LocateGames(problems);
    }

    private IEnumerable<GameLocatorResult> LocateGames(IGameLocatorProblemSink? problems)
    {
        var installed = HeroicConfigRoots.All(_fileSystem)
            .Select(root => (Root: root, Games: root.InstalledGogGames(_logger)))
            .ToArray();

        foreach (var result in _handler.FindAllGames())
        {
            if (result.TryPickT1(out var errorMessage, out var gameFinderGame))
            {
                _logger.LogWarning("Error locating games: {ErrorMessage}", errorMessage.Message);
                continue;
            }

            var storeIdentifier = gameFinderGame.Id.Value;
            _logger.LogDebug("Found game '{GameName}' with store identifier '{StoreIdentifier}'", gameFinderGame.Name, storeIdentifier);

            if (!_registeredGames.Games.TryGetValue(storeIdentifier, out var game)) continue;

            // The handler reads both Heroic installs and does not say which one a game came from. With the
            // game in both, nothing says which copy the user means, so locate neither and say why.
            var appName = storeIdentifier.ToString();
            var holders = installed
                .SelectMany(entry => entry.Games.Where(g => g.Key == appName), (entry, g) => (entry.Root, InstallPath: g.Value))
                .ToArray();
            if (holders.Select(holder => holder.Root).Distinct().Count() > 1)
            {
                var message = $"GOG product {storeIdentifier} ('{game.DisplayName}') is installed under both Heroic config roots: " +
                              string.Join(" and ", holders.Select(holder => $"'{holder.Root.Path}' ({holder.InstallPath})")) +
                              "; it is not located until it is removed from one";
                _logger.LogError("{Message}", message);
                problems?.Report(nameof(HeroicGOGLocator), message);
                continue;
            }

            var roots = holders.Select(holder => holder.Root).Distinct().ToImmutableArray();
            var settings = roots.Length == 1 ? HeroicGameSettings.Read(roots[0], appName, _logger) : null;
            var path = gameFinderGame.Path;
            var dlcIds = gameFinderGame.InstalledDLCs.Select(x => LocatorId.From(x.Value.ToString()));

            ImmutableArray<LocatorId> locatorIds =
            [
                LocatorId.From(gameFinderGame.BuildId.ToString()),
                ..dlcIds,
            ];

            yield return new GameLocatorResult
            {
                Game = game,
                Path = path,
                LocatorIds = locatorIds,
                StoreIdentifier = storeIdentifier.ToString(),
                Store = Store,
                Locator = this,
                Compatibility = GetCompatibilityLayer(gameFinderGame, roots, settings),
                Platform = gameFinderGame.Platform,
                WinePrefixPath = gameFinderGame.Platform == OSPlatform.Windows ? WinePrefix(gameFinderGame, settings) : null,
            };
        }
    }

    /// <summary>
    /// The prefix and runner Heroic keeps for the game, or null for a native Linux build or a game Heroic
    /// has no Wine settings for. Heroic's own settings file wins; the handler's reading fills what it lacks.
    /// </summary>
    private CompatibilityLayer? GetCompatibilityLayer(HeroicGOGGame game, ImmutableArray<HeroicConfigRoot> roots, HeroicGameSettings? settings)
    {
        if (game.Platform != OSPlatform.Windows) return null;

        var prefix = WinePrefix(game, settings);
        if (prefix is not { } winePrefix) return null;

        var kind = roots is [{ IsFlatpak: true }] ? CompatibilityLayerKind.HeroicFlatpak : CompatibilityLayerKind.Heroic;
        var bin = settings?.WineBin ?? game.WineData?.WineVersion.Bin;
        return new CompatibilityLayer(
            kind,
            winePrefix,
            bin is null ? null : _fileSystem.FromUnsanitizedFullPath(bin),
            settings?.WineName ?? game.WineData?.WineVersion.Name);
    }

    private AbsolutePath? WinePrefix(HeroicGOGGame game, HeroicGameSettings? settings)
    {
        if (settings?.WinePrefix is { } prefix) return _fileSystem.FromUnsanitizedFullPath(prefix);
        return game.WineData?.WinePrefixPath;
    }
}
