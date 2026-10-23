using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Games.Locators;

/// <summary>
/// Finds Epic games installed through Heroic's Legendary, from Legendary's own installed list rather than
/// the Windows launcher manifests the EGS locator reads.
/// </summary>
internal class HeroicEpicLocator : IGameLocator
{
    private readonly ILogger _logger;
    private readonly IFileSystem _fileSystem;
    private readonly StoreIdMap<string> _registeredGames;

    private static readonly GameStore Store = GameStore.EGS;

    public HeroicEpicLocator(IEnumerable<IGameData> games, ILoggerFactory loggerFactory, IFileSystem fileSystem)
    {
        _logger = loggerFactory.CreateLogger<HeroicEpicLocator>();
        _fileSystem = fileSystem;
        _registeredGames = StoreIdMap.Build(games, game => game.StoreIdentifiers.EGSCatalogItemId, "Epic catalog item", nameof(HeroicEpicLocator), _logger);
    }

    public IEnumerable<GameLocatorResult> Locate() => LocateGames(null);

    public IEnumerable<GameLocatorResult> Locate(IGameLocatorProblemSink problems)
    {
        _registeredGames.ReportTo(problems);
        return LocateGames(problems);
    }

    private IEnumerable<GameLocatorResult> LocateGames(IGameLocatorProblemSink? problems)
    {
        var installs = HeroicConfigRoots.All(_fileSystem)
            .SelectMany(root => root.InstalledEpicGames(_logger), (root, install) => (Root: root, Install: install))
            .GroupBy(entry => entry.Install.AppName);

        foreach (var holders in installs)
        {
            var appName = holders.Key;
            var roots = holders.Select(holder => holder.Root).Distinct().ToImmutableArray();

            // With the game in both Heroic installs nothing says which copy the user means, so locate
            // neither and say why. A game the app does not support is no one's problem.
            if (roots.Length > 1)
            {
                if (!roots.Any(r => TryMatch(r, appName, out _, out _))) continue;

                var message = $"Epic game {appName} is installed under both Heroic config roots: " +
                              string.Join(" and ", holders.Select(holder => $"'{holder.Root.Path}' ({holder.Install.InstallPath})")) +
                              "; it is not located until it is removed from one";
                _logger.LogError("{Message}", message);
                problems?.Report(nameof(HeroicEpicLocator), message);
                continue;
            }

            var root = roots[0];
            var install = holders.First().Install;

            if (!TryMatch(root, appName, out var storeIdentifier, out var game)) continue;

            var isWindows = !string.Equals(install.Platform, "Linux", StringComparison.OrdinalIgnoreCase);
            var settings = isWindows ? HeroicGameSettings.Read(root, appName, _logger) : null;
            var compatibility = settings?.WinePrefix is { } winePrefix
                ? CompatibilityLayer(root, _fileSystem.FromUnsanitizedFullPath(winePrefix), settings)
                : null;

            // LocatorIds stay empty: the file-hash lookup reads each id as a manifest hash, and nothing
            // Legendary keeps locally is one.
            yield return new GameLocatorResult
            {
                Game = game,
                Path = _fileSystem.FromUnsanitizedFullPath(install.InstallPath),
                LocatorIds = [],
                StoreIdentifier = storeIdentifier,
                Store = Store,
                Locator = this,
                Platform = isWindows ? OSPlatform.Windows : OSPlatform.Linux,
                WinePrefixPath = compatibility?.Prefix,
                Compatibility = compatibility,
            };
        }
    }

    /// <summary>
    /// The coded games mix both ids (Cyberpunk declares the catalog item id, Bannerlord the app name), so
    /// try the id Legendary's metadata gives, then the app name.
    /// </summary>
    private bool TryMatch(HeroicConfigRoot root, string appName, out string storeIdentifier, [NotNullWhen(true)] out IGameData? game)
    {
        var catalogItemId = root.EpicCatalogItemId(appName, _logger);
        if (catalogItemId is null)
            _logger.LogInformation("Legendary has no catalog item id for Epic game {AppName}; matching it by app name", appName);

        if (catalogItemId is not null && _registeredGames.Games.TryGetValue(catalogItemId, out game))
        {
            storeIdentifier = catalogItemId;
            return true;
        }

        storeIdentifier = appName;
        return _registeredGames.Games.TryGetValue(appName, out game);
    }

    private CompatibilityLayer CompatibilityLayer(HeroicConfigRoot root, AbsolutePath prefix, HeroicGameSettings settings)
    {
        var kind = root.IsFlatpak ? CompatibilityLayerKind.HeroicFlatpak : CompatibilityLayerKind.Heroic;
        return new CompatibilityLayer(kind, prefix, settings.WineBin is null ? null : _fileSystem.FromUnsanitizedFullPath(settings.WineBin), settings.WineName);
    }
}
