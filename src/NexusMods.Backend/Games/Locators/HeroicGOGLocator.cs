using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using GameFinder.Launcher.Heroic;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Backend.Games.Locators;

internal class HeroicGOGLocator : IGameLocator
{
    private readonly ILogger _logger;
    private readonly IFileSystem _fileSystem;
    private readonly HeroicGOGHandler _handler;
    private readonly FrozenDictionary<long, IGameData> _registeredGames;

    private static readonly GameStore Store = GameStore.GOG;

    public HeroicGOGLocator(IEnumerable<IGameData> games, ILoggerFactory loggerFactory, IFileSystem fileSystem)
    {
        _logger = loggerFactory.CreateLogger<HeroicGOGHandler>();
        _fileSystem = fileSystem;

        _handler = new HeroicGOGHandler(
            fileSystem: fileSystem,
            logger: loggerFactory.CreateLogger<HeroicGOGHandler>()
        );

        _registeredGames = games
            .SelectMany(game => game.StoreIdentifiers.GOGProductIds, (game, storeIdentifier) => new KeyValuePair<long, IGameData>(storeIdentifier, game))
            .ToFrozenDictionary();
    }

    public IEnumerable<GameLocatorResult> Locate()
    {
        foreach (var result in _handler.FindAllGames())
        {
            if (result.TryPickT1(out var errorMessage, out var gameFinderGame))
            {
                _logger.LogWarning("Error locating games: {ErrorMessage}", errorMessage.Message);
                continue;
            }

            var storeIdentifier = gameFinderGame.Id.Value;
            _logger.LogDebug("Found game '{GameName}' with store identifier '{StoreIdentifier}'", gameFinderGame.Name, storeIdentifier);

            if (!_registeredGames.TryGetValue(storeIdentifier, out var game)) continue;

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
                Compatibility = GetCompatibilityLayer(gameFinderGame),
                Platform = gameFinderGame.Platform,
            };
        }
    }

    /// <summary>
    /// The prefix and runner Heroic keeps for the game, or null for a native Linux build or a game Heroic
    /// has no Wine settings for.
    /// </summary>
    private CompatibilityLayer? GetCompatibilityLayer(HeroicGOGGame game)
    {
        if (game.Platform != OSPlatform.Windows || game.WineData is not { } wineData) return null;

        var kind = IsFlatpakInstall(game.Id.Value) ? CompatibilityLayerKind.HeroicFlatpak : CompatibilityLayerKind.Heroic;
        return new CompatibilityLayer(kind, wineData.WinePrefixPath, _fileSystem.FromUnsanitizedFullPath(wineData.WineVersion.Bin), wineData.WineVersion.Name);
    }

    /// <summary>
    /// The handler does not say which Heroic installation it read, so look for the game's own settings file
    /// in both config roots. A game with settings in both is attributed to the native install.
    /// </summary>
    private bool IsFlatpakInstall(long productId)
    {
        var settings = RelativePath.FromUnsanitizedInput($"GamesConfig/{productId}.json");
        var native = _fileSystem.GetKnownPath(KnownPath.XDG_CONFIG_HOME).Combine("heroic").Combine(settings);
        var flatpak = _fileSystem.GetKnownPath(KnownPath.HomeDirectory).Combine(".var/app/com.heroicgameslauncher.hgl/config/heroic").Combine(settings);
        return !native.FileExists && flatpak.FileExists;
    }
}
