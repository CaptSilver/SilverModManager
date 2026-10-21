using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Backend.Games.Locators;

/// <summary>
/// Finds games Lutris manages, from its <c>pga.db</c> and each game's YAML under both the native and the
/// Flatpak install. A GOG or Epic game Lutris synced is reported under its own store; a Wine game added by
/// hand is recognised by its executable and reported as <see cref="GameStore.Lutris"/>.
/// </summary>
internal class LutrisLocator : IGameLocator
{
    private readonly ILogger _logger;
    private readonly IFileSystem _fileSystem;
    private readonly ImmutableArray<LutrisRoot> _roots;
    private readonly StoreIdMap<long> _gogGames;
    private readonly StoreIdMap<string> _epicGames;
    private readonly StoreIdMap<string> _executableGames;
    private readonly Dictionary<IGameData, RelativePath> _primaryFiles;

    public LutrisLocator(IEnumerable<IGameData> games, ILoggerFactory loggerFactory, IFileSystem fileSystem)
        : this(games, loggerFactory, fileSystem, LutrisRoot.All(fileSystem)) { }

    public LutrisLocator(IEnumerable<IGameData> games, ILoggerFactory loggerFactory, IFileSystem fileSystem, ImmutableArray<LutrisRoot> roots)
    {
        _logger = loggerFactory.CreateLogger<LutrisLocator>();
        _fileSystem = fileSystem;
        _roots = roots;

        var all = games.ToArray();
        _primaryFiles = new Dictionary<IGameData, RelativePath>(ReferenceEqualityComparer.Instance);
        foreach (var game in all)
        {
            if (PrimaryFile(game) is { } primaryFile) _primaryFiles[game] = primaryFile;
        }

        _gogGames = StoreIdMap.Build(all, game => game.StoreIdentifiers.GOGProductIds, "GOG product", nameof(LutrisLocator), _logger);
        _epicGames = StoreIdMap.Build(all, game => game.StoreIdentifiers.EGSCatalogItemId, "Epic catalog item", nameof(LutrisLocator), _logger);
        _executableGames = StoreIdMap.Build<string>(
            all,
            game => _primaryFiles.TryGetValue(game, out var primaryFile) ? [primaryFile.FileName.ToString().ToLowerInvariant()] : [],
            "executable name",
            nameof(LutrisLocator),
            _logger
        );
    }

    public IEnumerable<GameLocatorResult> Locate() => LocateGames(null);

    public IEnumerable<GameLocatorResult> Locate(IGameLocatorProblemSink problems)
    {
        _gogGames.ReportTo(problems);
        _epicGames.ReportTo(problems);
        _executableGames.ReportTo(problems);
        return LocateGames(problems);
    }

    private sealed record Candidate(LutrisRoot Root, LutrisRow Row, LutrisGameConfig Config, IGameData Game, GameStore Store, string StoreIdentifier);

    private IEnumerable<GameLocatorResult> LocateGames(IGameLocatorProblemSink? problems)
    {
        var candidates = _roots
            .SelectMany(root => root.InstalledGames(_logger).Select(row => (Root: root, Row: row)))
            .Select(entry => TryMatch(entry.Root, entry.Row))
            .OfType<Candidate>()
            .GroupBy(candidate => candidate.Row.Slug);

        foreach (var holders in candidates)
        {
            // With the game in both Lutris installs nothing says which copy the user means, so locate
            // neither and say why.
            if (holders.Select(holder => holder.Root).Distinct().Count() > 1)
            {
                var message = $"Lutris game {holders.Key} is installed under both Lutris data roots: " +
                              string.Join(" and ", holders.Select(holder => $"'{holder.Root.DataDir}' ({holder.Row.Directory ?? holder.Config.Exe})")) +
                              "; it is not located until it is removed from one";
                _logger.LogError("{Message}", message);
                problems?.Report(nameof(LutrisLocator), message);
                continue;
            }

            var result = TryBuildResult(holders.First());
            if (result is not null) yield return result;
        }
    }

    /// <summary>
    /// The game this row describes, or null when no registered game claims it or its settings cannot be read.
    /// </summary>
    private Candidate? TryMatch(LutrisRoot root, LutrisRow row)
    {
        IGameData? game;
        GameStore store;
        string storeIdentifier = row.ServiceId ?? row.Slug;

        if (string.Equals(row.Service, "gog", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(row.ServiceId, out var productId) || !_gogGames.Games.TryGetValue(productId, out game)) return null;
            store = GameStore.GOG;
        }
        else if (string.Equals(row.Service, "egs", StringComparison.OrdinalIgnoreCase))
        {
            if (row.ServiceId is null || !_epicGames.Games.TryGetValue(row.ServiceId, out game)) return null;
            store = GameStore.EGS;
        }
        else if (row.Service is null && string.Equals(row.Runner, "wine", StringComparison.OrdinalIgnoreCase))
        {
            // Only the YAML names the executable, so it is read before knowing whether anyone claims the game.
            var config = root.ReadGameConfig(row, _logger);
            if (config?.Exe is null) return null;
            if (!_executableGames.Games.TryGetValue(FileName(config.Exe).ToLowerInvariant(), out game)) return null;
            return new Candidate(root, row, config, game, GameStore.Lutris, row.Slug);
        }
        else
        {
            return null;
        }

        var gameConfig = root.ReadGameConfig(row, _logger);
        return gameConfig is null ? null : new Candidate(root, row, gameConfig, game, store, storeIdentifier);
    }

    private GameLocatorResult? TryBuildResult(Candidate candidate)
    {
        var (root, row, config, game, store, storeIdentifier) = candidate;
        var isWine = string.Equals(row.Runner, "wine", StringComparison.OrdinalIgnoreCase);

        try
        {
            var installPath = InstallPath(row, config, game);
            if (installPath is null)
            {
                _logger.LogWarning("Lutris game {Slug} has no install directory and its executable does not say where it is; skipping it", row.Slug);
                return null;
            }

            CompatibilityLayer? compatibility = null;
            if (isWine && config.Prefix is { } prefix)
            {
                var wineBinary = config.WineVersion is null ? (AbsolutePath?)null : root.WineBinary(config.WineVersion);
                compatibility = new CompatibilityLayer(
                    CompatibilityLayerKind.PlainWine,
                    _fileSystem.FromUnsanitizedFullPath(prefix),
                    wineBinary is { } binary && binary.FileExists ? binary : null,
                    config.WineVersion is null ? null : $"lutris:{config.WineVersion}"
                );
            }

            return new GameLocatorResult
            {
                Game = game,
                Path = installPath.Value,
                // Lutris keeps no build or manifest id the file-hash lookup could use.
                LocatorIds = [],
                StoreIdentifier = storeIdentifier,
                Store = store,
                Locator = this,
                Platform = isWine ? OSPlatform.Windows : OSPlatform.Linux,
                WinePrefixPath = compatibility?.Prefix,
                Compatibility = compatibility,
            };
        }
        catch (ArgumentException e)
        {
            _logger.LogWarning(e, "Skipping Lutris game {Slug}: its recorded paths are not usable", row.Slug);
            return null;
        }
    }

    /// <summary>
    /// The game's root folder. Neither recorded path names it reliably: Lutris's own Wine installers make the game
    /// directory the prefix and put the game under <c>drive_c</c>, and the executable can sit below the root
    /// (<c>bin/x64</c>, a launcher folder). So the root is the first of the directory and the executable's
    /// ancestors that holds the game's primary file, falling back to the recorded paths when none does.
    /// </summary>
    private AbsolutePath? InstallPath(LutrisRow row, LutrisGameConfig config, IGameData game)
    {
        var directory = row.Directory is null ? (AbsolutePath?)null : _fileSystem.FromUnsanitizedFullPath(row.Directory);
        var prefix = config.Prefix is null ? (AbsolutePath?)null : _fileSystem.FromUnsanitizedFullPath(config.Prefix);
        var exe = ResolveExe(config, directory, prefix);

        if (_primaryFiles.TryGetValue(game, out var primaryFile))
        {
            foreach (var folder in RootCandidates(directory, prefix, exe))
            {
                if (folder.Combine(primaryFile).FileExists) return folder;
            }
        }

        if (directory is { } dir && dir != prefix) return dir;
        if (exe is { } exePath) return exePath.Parent;
        if (config.WorkingDir is { } workingDir) return _fileSystem.FromUnsanitizedFullPath(workingDir);
        return directory;
    }

    /// <summary>
    /// The executable as a host path. Lutris stores it as a host path, relative to the game directory, or, for
    /// a Wine game, as a Windows path, where C: is the prefix's <c>drive_c</c> and Z: the host root.
    /// </summary>
    private AbsolutePath? ResolveExe(LutrisGameConfig config, AbsolutePath? directory, AbsolutePath? prefix)
    {
        if (config.Exe is not { } exe) return null;
        if (exe.StartsWith('/')) return _fileSystem.FromUnsanitizedFullPath(exe);

        if (exe.Length >= 3 && char.IsAsciiLetter(exe[0]) && exe[1] == ':' && exe[2] is '\\' or '/')
        {
            var rest = exe[3..].Replace('\\', '/');
            return char.ToLowerInvariant(exe[0]) switch
            {
                'z' => _fileSystem.FromUnsanitizedFullPath("/" + rest),
                'c' when prefix is { } prefixPath => prefixPath.Combine("drive_c").Combine(rest),
                _ => null,
            };
        }

        var baseFolder = directory ?? (config.WorkingDir is null ? null : _fileSystem.FromUnsanitizedFullPath(config.WorkingDir));
        return baseFolder?.Combine(exe);
    }

    /// <summary>
    /// The directory, then the executable's folder and its ancestors up to the directory or prefix that holds
    /// it, or to the host root when neither does.
    /// </summary>
    private static IEnumerable<AbsolutePath> RootCandidates(AbsolutePath? directory, AbsolutePath? prefix, AbsolutePath? exe)
    {
        if (directory is { } dir) yield return dir;
        if (exe is not { } exePath) yield break;

        var folder = exePath.Parent;
        while (true)
        {
            yield return folder;
            if (folder == directory || folder == prefix) yield break;

            var parent = folder.Parent;
            // Stops at the root whatever Parent returns there.
            if (parent.GetFullPath().Length >= folder.GetFullPath().Length) yield break;
            folder = parent;
        }
    }

    /// <summary>
    /// The path of the file a game launches, relative to its root. A Wine game with no store service is
    /// recognised by this file's name, and every game's root is found by looking for it. It is asked for through
    /// a probe installation because <see cref="IGameData.GetPrimaryFile"/> needs one; games only look at the
    /// target OS and store there, so a Windows install under Lutris answers it.
    /// </summary>
    private RelativePath? PrimaryFile(IGameData game)
    {
        try
        {
            var probePath = _fileSystem.GetKnownPath(KnownPath.HomeDirectory);
            var probe = new GameLocatorResult
            {
                Game = game,
                Path = probePath,
                Store = GameStore.Lutris,
                StoreIdentifier = "",
                LocatorIds = [],
                Locator = this,
                Platform = OSPlatform.Windows,
            };
            var installation = new GameInstallation(probe, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, probePath)));
            var primaryFile = game.GetPrimaryFile(installation);
            if (primaryFile.LocationId == LocationId.Game) return primaryFile.Path;

            _logger.LogWarning("{Game} launches a file outside its game folder; Lutris games will not be matched to it by name", game.DisplayName);
            return null;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Cannot tell which executable {Game} launches; Lutris games will not be matched to it by name", game.DisplayName);
            return null;
        }
    }

    private static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
}
