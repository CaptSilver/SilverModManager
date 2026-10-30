using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
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
        var allGames = FindAllGames().ToArray();
        var libraries = allGames.Select(g => g.LibraryFolder.Path).Concat(allGames.Select(g => g.SteamPath)).Distinct().ToArray();

        foreach (var gameFinderGame in allGames)
        {
            var storeIdentifier = gameFinderGame.AppId.Value;
            _logger.LogDebug("Found game '{GameName}' with store identifier '{StoreIdentifier}'", gameFinderGame.Name, storeIdentifier);

            if (!_registeredGames.Games.TryGetValue(storeIdentifier, out var game)) continue;

            yield return ToResult(game, gameFinderGame, libraries);
        }
    }

    /// <summary>
    /// Locates one game by reading its own appmanifest, not the manifest of every game in every library as
    /// <see cref="Locate()"/> does. Steam rewrites a manifest again and again while it downloads, and the app
    /// looks at the game on each of those writes, so the full walk cost seconds each time.
    /// </summary>
    public bool TryLocate(IGameData game, [NotNullWhen(true)] out GameLocatorResult? locatorResult)
    {
        locatorResult = null;

        // Locate gives an id to the first game registered for it; a later game claiming it is never found.
        var appIds = game.StoreIdentifiers.SteamAppIds
            .Where(id => _registeredGames.Games.TryGetValue(id, out var owner) && owner.GameId == game.GameId)
            .ToArray();
        if (appIds.Length == 0) return false;

        if (!TryReadLibraryFolders(out var steamPath, out var libraryFolders)) return false;
        var libraries = libraryFolders.Select(l => l.Path).Append(steamPath).Distinct().ToArray();

        foreach (var library in libraryFolders)
        {
            foreach (var appId in appIds)
            {
                if (!TryReadManifest(library, $"appmanifest_{appId}.acf", out var manifest)) continue;
                var installed = new SteamGame { SteamPath = steamPath, AppManifest = manifest, LibraryFolder = library };
                locatorResult = ToResult(game, installed, libraries);
                return true;
            }
        }

        return false;
    }

    private GameLocatorResult ToResult(IGameData game, SteamGame gameFinderGame, IReadOnlyList<AbsolutePath> libraries)
    {
        var locatorIds = gameFinderGame.AppManifest.InstalledDepots
            .Select(x => LocatorId.From(x.Value.ManifestId.Value.ToString()))
            .ToImmutableArray();

        // GetProtonPrefix() only checks that compatdata/<appid> exists, which Steam creates before
        // Proton has written anything into it. Build the prefix from the manifest instead and ask
        // ProtonPrefix.IsReady, so a game that was never launched still resolves its known folders
        // into the prefix it will get rather than silently into the host's home.
        var protonDirectory = CompatDataDirectory(gameFinderGame, libraries);
        var prefix = new ProtonWinePrefix { ProtonDirectory = protonDirectory, ConfigurationDirectory = protonDirectory.Combine("pfx") };
        var prefixReady = ProtonPrefix.IsReady(prefix.ConfigurationDirectory);

        // A prefix outlives a switch to the native build, so a ready one only means Windows when the
        // game isn't installed in its native form. Without a prefix, a game with a native build is it.
        var isNativeBuild = game.HasNativeLinuxBuild && (!prefixReady || IsNativeInstall(game, gameFinderGame));
        var isWindowsBuild = !isNativeBuild;
        if (isNativeBuild && prefixReady) LogStalePrefix(gameFinderGame, prefix.ConfigurationDirectory);

        var fileSystem = isWindowsBuild ? prefix.CreateOverlayFileSystem(_fileSystem) : _fileSystem;
        var path = fileSystem.FromUnsanitizedFullPath(gameFinderGame.Path.GetFullPath());
        var linuxCompatibilityDataProvider = prefixReady && isWindowsBuild ? new LinuxCompatibilityDataProvider(gameFinderGame, prefix.ConfigurationDirectory) : null;

        return new GameLocatorResult
        {
            Game = game,
            Path = path,
            LocatorIds = locatorIds,
            Platform = isWindowsBuild ? OSPlatform.Windows : OSPlatform.Linux,
            WinePrefixPath = isWindowsBuild ? prefix.ConfigurationDirectory : null,
            StoreIdentifier = gameFinderGame.AppId.Value.ToString(),
            Store = Store,
            Locator = this,
            LinuxCompatabilityDataProvider = linuxCompatibilityDataProvider,
            Compatibility = isWindowsBuild ? new CompatibilityLayer(CompatibilityLayerKind.SteamProton, prefix.ConfigurationDirectory) : null,
            StoreRoot = gameFinderGame.SteamPath,
            StoreBuildId = gameFinderGame.AppManifest.BuildId.Value.ToString(),
            StoreTargetBuildId = TargetBuildOf(gameFinderGame.AppManifest),
            // A settled install's manifest holds FullyInstalled alone; any other flag (update required,
            // running, paused, files missing) means the files may not match the listed build.
            StoreUpdatePending = gameFinderGame.AppManifest.StateFlags != StateFlags.FullyInstalled,
            StoreUpdateInProgress = (gameFinderGame.AppManifest.StateFlags & UpdateInProgressFlags) != 0,
            StoreFilesDamaged = (gameFinderGame.AppManifest.StateFlags & (StateFlags.FilesMissing | StateFlags.FilesCorrupt)) != 0,
            StoreLastWrite = LastWriteOf(gameFinderGame.AppManifest),
            StoreUpdatesInBackground = gameFinderGame.AppManifest.AutoUpdateBehavior != AutoUpdateBehavior.UpdateOnLaunch,
        };
    }

    // Flags Steam sets while it writes the installed files. UpdateRequired alone is not one: it stays set for
    // players who update only on launch.
    private const StateFlags UpdateInProgressFlags = StateFlags.UpdateRunning | StateFlags.UpdateStarted | StateFlags.UpdatePaused
        | StateFlags.Downloading | StateFlags.Staging | StateFlags.Committing | StateFlags.Preallocating
        | StateFlags.AddingFiles | StateFlags.Reconfiguring | StateFlags.Validating | StateFlags.UpdateStopping | StateFlags.Uninstalling;

    private readonly HashSet<uint> _loggedStalePrefixes = [];

    private bool IsNativeInstall(IGameData game, SteamGame installed)
    {
        try
        {
            return game.IsInstalledAt(_fileSystem.FromUnsanitizedFullPath(installed.Path.GetFullPath()));
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            // Can't tell what is installed; the prefix is the only evidence left.
            return false;
        }
    }

    // The prefix belongs to the user and may hold saves from earlier Proton runs, so it is only mentioned.
    private void LogStalePrefix(SteamGame game, AbsolutePath prefix)
    {
        lock (_loggedStalePrefixes)
        {
            if (!_loggedStalePrefixes.Add(game.AppId.Value)) return;
        }

        _logger.LogWarning("{GameName} is installed as its native Linux build, so the Proton prefix at {Prefix} is not used. Its files are left as they are.", game.Name, prefix);
    }

    /// <summary>
    /// The game's compatdata folder. Steam moves a game's files to another library but leaves compatdata/&lt;appid&gt;
    /// in the library where Proton first created it, so when the game's own library holds no ready prefix, a ready one
    /// in another library is used. With none ready anywhere, the game's own library is where the prefix will appear.
    /// </summary>
    private AbsolutePath CompatDataDirectory(SteamGame game, IReadOnlyList<AbsolutePath> libraries)
    {
        var own = game.AppManifest.GetCompatabilityDataDirectoryPath();
        if (ProtonPrefix.IsReady(own.Combine("pfx"))) return own;

        var relative = own.RelativeTo(game.LibraryFolder.Path);
        foreach (var library in libraries)
        {
            var candidate = library.Combine(relative);
            try
            {
                if (ProtonPrefix.IsReady(candidate.Combine("pfx")))
                {
                    _logger.LogDebug("Found the Proton prefix of {AppId} in another library: {Path}", game.AppId, candidate);
                    return candidate;
                }
            }
            catch (Exception e) when (IsStorageFailure(e))
            {
                // A library on an unplugged drive can't hold the prefix we need.
            }
        }

        return own;
    }


    /// <summary>
    /// Walks every library folder in <c>libraryfolders.vdf</c>, as GameFinder's <c>SteamHandler.FindAllGames</c> does,
    /// but keeps a library that cannot be read from taking the others with it. The handler is an iterator, so one
    /// exception ends the whole enumeration. A library on a drive that is unplugged behind an automount fails with
    /// an <see cref="IOException"/> such as ENODEV rather than reporting that the path is missing.
    /// </summary>
    private IEnumerable<SteamGame> FindAllGames()
    {
        if (!TryReadLibraryFolders(out var steamPath, out var libraryFolders)) return [];
        return libraryFolders.SelectMany(library => ReadLibrary(steamPath, library));
    }

    private bool TryReadLibraryFolders(out AbsolutePath steamPath, [NotNullWhen(true)] out LibraryFoldersManifest? libraryFolders)
    {
        steamPath = default;
        libraryFolders = null;
        try
        {
            if (!SteamLocationFinder.TryFindSteam(_fileSystem, _registry, _logger, out steamPath)) return false;

            var libraryFoldersFilePath = SteamLocationFinder.GetLibraryFoldersFilePath(steamPath);
            var parsed = LibraryFoldersManifestParser.ParseManifestFile(libraryFoldersFilePath);
            if (parsed.IsFailed)
            {
                _logger.LogWarning("Failed to parse library folders file '{FilePath}': {Errors}", libraryFoldersFilePath, parsed.Errors);
                return false;
            }

            libraryFolders = parsed.Value;
            return true;
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            _logger.LogWarning(e, "Could not read Steam's library folders");
            return false;
        }
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
                if (TryReadManifest(manifestPath, out var manifest))
                    games.Add(new SteamGame { SteamPath = steamPath, AppManifest = manifest, LibraryFolder = library });
            }

            return games;
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            _logger.LogWarning(e, "Skipping Steam library folder '{Path}', which could not be read", library.Path);
            return [];
        }
    }

    private bool TryReadManifest(LibraryFolder library, string fileName, [NotNullWhen(true)] out AppManifest? manifest)
        => TryReadManifest(library.Path.Combine("steamapps").Combine(fileName), out manifest);

    // One unreadable or half-written manifest must not hide the rest of the library.
    private bool TryReadManifest(AbsolutePath manifestPath, [NotNullWhen(true)] out AppManifest? manifest)
    {
        manifest = null;
        try
        {
            if (!_fileSystem.FileExists(manifestPath)) return false;
            var parsed = AppManifestParser.ParseManifestFile(manifestPath);
            if (parsed.IsFailed)
            {
                _logger.LogWarning("Failed to parse app manifest file '{Path}': {Errors}", manifestPath, parsed.Errors);
                return false;
            }

            manifest = parsed.Value;
            return true;
        }
        catch (Exception e) when (IsStorageFailure(e))
        {
            _logger.LogWarning(e, "Skipping unreadable app manifest '{Path}'", manifestPath);
            return false;
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

    /// <summary>
    /// The build Steam is updating to, or null when the manifest names none (<c>TargetBuildID</c> 0).
    /// </summary>
    internal static string? TargetBuildOf(AppManifest manifest)
        => manifest.TargetBuildId.Value == 0 ? null : manifest.TargetBuildId.Value.ToString();

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
