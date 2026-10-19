using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using GameFinder.StoreHandlers.Steam;
using GameFinder.StoreHandlers.Steam.Services;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.Backend.Games.Locators;

internal class SteamLocator : IGameLocator
{
    private readonly ILogger _logger;
    private readonly IFileSystem _fileSystem;
    private readonly SteamHandler _handler;
    private readonly FrozenDictionary<uint, IGameData> _registeredGames;

    private static readonly GameStore Store = GameStore.Steam;

    public SteamLocator(IEnumerable<IGameData> games, ILoggerFactory loggerFactory, IFileSystem fileSystem, GameFinder.RegistryUtils.IRegistry? registry)
    {
        _logger = loggerFactory.CreateLogger<SteamLocator>();
        _fileSystem = fileSystem;

        _handler = new SteamHandler(
            fileSystem: fileSystem,
            registry: registry,
            logger: loggerFactory.CreateLogger<SteamHandler>()
        );

        _registeredGames = games
            .SelectMany(game => game.StoreIdentifiers.SteamAppIds, (game, storeIdentifier) => new KeyValuePair<uint, IGameData>(storeIdentifier, game))
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

            var storeIdentifier = gameFinderGame.AppId.Value;
            _logger.LogDebug("Found game '{GameName}' with store identifier '{StoreIdentifier}'", gameFinderGame.Name, storeIdentifier);

            if (!_registeredGames.TryGetValue(storeIdentifier, out var game)) continue;

            var locatorIds = gameFinderGame.AppManifest.InstalledDepots
                .Select(x => LocatorId.From(x.Value.ManifestId.Value.ToString()))
                .ToImmutableArray();

            // GetProtonPrefix() only checks that compatdata/<appid> exists, which Steam creates before
            // Proton has written anything into it. Build the prefix from the manifest instead and ask
            // ProtonPrefix.IsReady, so a game that was never launched still resolves its known folders
            // into the prefix it will get rather than silently into the host's home.
            var protonDirectory = gameFinderGame.AppManifest.GetCompatabilityDataDirectoryPath();
            var prefix = new ProtonWinePrefix { ProtonDirectory = protonDirectory, ConfigurationDirectory = protonDirectory.Combine("pfx") };
            var prefixReady = ProtonPrefix.IsReady(prefix.ConfigurationDirectory);
            var isWindowsBuild = prefixReady || !game.HasNativeLinuxBuild;

            var fileSystem = isWindowsBuild ? prefix.CreateOverlayFileSystem(_fileSystem) : _fileSystem;
            var path = fileSystem.FromUnsanitizedFullPath(gameFinderGame.Path.GetFullPath());
            var linuxCompatibilityDataProvider = prefixReady ? new LinuxCompatibilityDataProvider(gameFinderGame, prefix.ConfigurationDirectory) : null;

            yield return new GameLocatorResult
            {
                Game = game,
                Path = path,
                LocatorIds = locatorIds,
                Platform = isWindowsBuild ? OSPlatform.Windows : OSPlatform.Linux,
                WinePrefixPath = isWindowsBuild ? prefix.ConfigurationDirectory : null,
                StoreIdentifier = storeIdentifier.ToString(),
                Store = Store,
                Locator = this,
                LinuxCompatabilityDataProvider = linuxCompatibilityDataProvider,
            };
        }
    }

    private class LinuxCompatibilityDataProvider : ILinuxCompatabilityDataProvider
    {
        private readonly SteamGame _steamGame;

        public AbsolutePath WinePrefixDirectoryPath { get; }

        public LinuxCompatibilityDataProvider(SteamGame steamGame, AbsolutePath winePrefixDirectoryPath)
        {
            _steamGame = steamGame;
            WinePrefixDirectoryPath = winePrefixDirectoryPath;
        }

        public ValueTask<ImmutableHashSet<string>> GetInstalledWinetricksComponents(CancellationToken cancellationToken)
        {
            var filePath = WineParser.GetWinetricksLogFilePath(WinePrefixDirectoryPath);
            var result = WineParser.ParseWinetricksLogFile(filePath);
            return new ValueTask<ImmutableHashSet<string>>(result);
        }

        public ValueTask<ImmutableArray<WineDllOverride>> GetWineDllOverrides(CancellationToken cancellationToken)
        {
            var localConfigPath = SteamLocationFinder.GetUserDataDirectoryPath(_steamGame.SteamPath, _steamGame.AppManifest.LastOwner).Combine("config").Combine("localconfig.vdf");
            var parserResult = LocalUserConfigParser.ParseConfigFile(_steamGame.AppManifest.LastOwner, localConfigPath);
            if (parserResult.IsFailed || !parserResult.Value.LocalAppData.TryGetValue(_steamGame.AppId, out var localAppData))
                return new ValueTask<ImmutableArray<WineDllOverride>>(ImmutableArray<WineDllOverride>.Empty);

            var launchOptions = localAppData.LaunchOptions;
            var section = WineParser.GetWineDllOverridesSection(launchOptions);
            var result = WineParser.ParseEnvironmentVariable(section);
            return new ValueTask<ImmutableArray<WineDllOverride>>(result);
        }
    }
}
