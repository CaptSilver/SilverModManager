using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using GameFinder.StoreHandlers.Steam;
using GameFinder.StoreHandlers.Steam.Models;
using GameFinder.StoreHandlers.Steam.Services;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Steam;

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
                Compatibility = isWindowsBuild ? new CompatibilityLayer(CompatibilityLayerKind.SteamProton, prefix.ConfigurationDirectory) : null,
                StoreRoot = gameFinderGame.SteamPath,
                StoreBuildId = gameFinderGame.AppManifest.BuildId.Value.ToString(),
                // A settled install's manifest holds FullyInstalled alone; any other flag (update required,
                // running, paused, files missing) means the files may not match the listed build.
                StoreUpdatePending = gameFinderGame.AppManifest.StateFlags != StateFlags.FullyInstalled,
                StoreLastWrite = LastWriteOf(gameFinderGame.AppManifest),
            };
        }
    }

    /// <summary>
    /// The appmanifest's own update time when it has one; many manifests don't (Cyberpunk 2077's has no
    /// LastUpdated key), and then the file's modification time, which Steam sets after the last game file
    /// is written. The depot manifests can't stand in: they are cached before the download starts.
    /// </summary>
    internal static DateTimeOffset? LastWriteOf(AppManifest manifest)
    {
        if (manifest.LastUpdated > DateTimeOffset.UnixEpoch) return manifest.LastUpdated;
        return manifest.ManifestPath.FileExists ? new DateTimeOffset(manifest.ManifestPath.FileInfo.LastWriteTimeUtc, TimeSpan.Zero) : null;
    }

    private class LinuxCompatibilityDataProvider : ILinuxCompatabilityDataProvider
    {
        private readonly SteamGame _steamGame;

        public AbsolutePath WinePrefixDirectoryPath { get; }

        public AbsolutePath SteamInstallPath => _steamGame.SteamPath;

        public AbsolutePath SteamLocalConfigPath => SteamLocationFinder
            .GetUserDataDirectoryPath(_steamGame.SteamPath, _steamGame.AppManifest.LastOwner)
            .Combine("config").Combine("localconfig.vdf");

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
            // Not GameFinder's LocalUserConfigParser: it rejects the whole file at the first non-Steam shortcut,
            // whose app id Steam stores as a negative number.
            var launchOptions = LocalConfigLaunchOptions.Read(SteamLocalConfigPath, _steamGame.AppId.Value).Line;
            if (launchOptions is null) return new ValueTask<ImmutableArray<WineDllOverride>>(ImmutableArray<WineDllOverride>.Empty);

            var section = WineParser.GetWineDllOverridesSection(launchOptions);
            var result = WineParser.ParseEnvironmentVariable(section);
            return new ValueTask<ImmutableArray<WineDllOverride>>(result);
        }
    }
}
