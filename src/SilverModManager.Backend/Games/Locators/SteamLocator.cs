using System.Collections.Immutable;
using System.Runtime.InteropServices;
using GameFinder.StoreHandlers.Steam;
using GameFinder.StoreHandlers.Steam.Models;
using GameFinder.StoreHandlers.Steam.Services;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Steam;

namespace SilverModManager.Backend.Games.Locators;

internal class SteamLocator : IGameLocator
{
    private readonly ILogger _logger;
    private readonly IFileSystem _fileSystem;
    private readonly GameFinder.RegistryUtils.IRegistry? _registry;
    private readonly StoreIdMap<uint> _registeredGames;

    private static readonly GameStore Store = GameStore.Steam;

    public SteamLocator(IEnumerable<IGameData> games, ILoggerFactory loggerFactory, IFileSystem fileSystem, GameFinder.RegistryUtils.IRegistry? registry)
    {
        _logger = loggerFactory.CreateLogger<SteamLocator>();
        _fileSystem = fileSystem;

        _registry = registry;

        _registeredGames = StoreIdMap.Build(games, game => game.StoreIdentifiers.SteamAppIds, "Steam app", nameof(SteamLocator), _logger);
    }

    public IEnumerable<GameLocatorResult> Locate() => LocateGames();

    public IEnumerable<GameLocatorResult> Locate(IGameLocatorProblemSink problems)
    {
        _registeredGames.ReportTo(problems);
        return LocateGames();
    }

    private IEnumerable<GameLocatorResult> LocateGames()
    {
        foreach (var gameFinderGame in FindAllGames())
        {
            var storeIdentifier = gameFinderGame.AppId.Value;
            _logger.LogDebug("Found game '{GameName}' with store identifier '{StoreIdentifier}'", gameFinderGame.Name, storeIdentifier);

            if (!_registeredGames.Games.TryGetValue(storeIdentifier, out var game)) continue;

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
    /// Walks every library folder in <c>libraryfolders.vdf</c>, as GameFinder's <c>SteamHandler.FindAllGames</c> does,
    /// but keeps a library that cannot be read from taking the others with it. The handler is an iterator, so one
    /// exception ends the whole enumeration. A library on a drive that is unplugged behind an automount fails with
    /// an <see cref="IOException"/> such as ENODEV rather than reporting that the path is missing.
    /// </summary>
    private IEnumerable<SteamGame> FindAllGames()
    {
        AbsolutePath steamPath;
        LibraryFoldersManifest libraryFolders;
        try
        {
            if (!SteamLocationFinder.TryFindSteam(_fileSystem, _registry, _logger, out steamPath)) return [];

            var libraryFoldersFilePath = SteamLocationFinder.GetLibraryFoldersFilePath(steamPath);
            var parsed = LibraryFoldersManifestParser.ParseManifestFile(libraryFoldersFilePath);
            if (parsed.IsFailed)
            {
                _logger.LogWarning("Failed to parse library folders file '{FilePath}': {Errors}", libraryFoldersFilePath, parsed.Errors);
                return [];
            }

            libraryFolders = parsed.Value;
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            _logger.LogWarning(e, "Could not read Steam's library folders");
            return [];
        }

        return libraryFolders.SelectMany(library => ReadLibrary(steamPath, library));
    }

    private IEnumerable<SteamGame> ReadLibrary(AbsolutePath steamPath, LibraryFolder library)
    {
        try
        {
            if (!_fileSystem.DirectoryExists(library.Path))
            {
                _logger.LogWarning("Steam library folder at '{Path}' doesn't exist", library.Path);
                return [];
            }

            var games = new List<SteamGame>();
            foreach (var manifestPath in library.EnumerateAppManifestFilePaths().ToArray())
            {
                // One unreadable manifest must not hide the rest of the library.
                try
                {
                    var manifest = AppManifestParser.ParseManifestFile(manifestPath);
                    if (manifest.IsFailed)
                    {
                        _logger.LogWarning("Failed to parse app manifest file '{Path}': {Errors}", manifestPath, manifest.Errors);
                        continue;
                    }

                    games.Add(new SteamGame { SteamPath = steamPath, AppManifest = manifest.Value, LibraryFolder = library });
                }
                catch (Exception e) when (IsStorageFailure(e))
                {
                    _logger.LogWarning(e, "Skipping unreadable app manifest '{Path}'", manifestPath);
                }
            }

            return games;
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            _logger.LogWarning(e, "Skipping Steam library folder '{Path}', which could not be read", library.Path);
            return [];
        }
    }

    private static bool IsStorageFailure(Exception e) => e is IOException or UnauthorizedAccessException;

    /// <summary>
    /// The appmanifest's update time (Steam writes it as <c>lastupdated</c>), or null when it has none. The .acf
    /// file's modification time can't stand in: Steam rewrites the file on every launch. Nor can the depot
    /// manifests: they are cached before the download starts.
    /// </summary>
    internal static DateTimeOffset? LastWriteOf(AppManifest manifest)
        => manifest.LastUpdated > DateTimeOffset.UnixEpoch ? manifest.LastUpdated : null;

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
