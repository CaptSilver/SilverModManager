using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Games.Locators;

/// <summary>
/// Finds Amazon games installed through Heroic's Nile, from Nile's installed list under both Heroic config roots.
/// </summary>
internal class HeroicAmazonLocator : IGameLocator
{
    private readonly ILogger _logger;
    private readonly IFileSystem _fileSystem;
    private readonly StoreIdMap<string> _registeredGames;

    private static readonly GameStore Store = GameStore.Amazon;

    public HeroicAmazonLocator(IEnumerable<IGameData> games, ILoggerFactory loggerFactory, IFileSystem fileSystem)
    {
        _logger = loggerFactory.CreateLogger<HeroicAmazonLocator>();
        _fileSystem = fileSystem;
        _registeredGames = StoreIdMap.Build(games, game => game.StoreIdentifiers.AmazonProductIds, "Amazon product", nameof(HeroicAmazonLocator), _logger);
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
            .SelectMany(root => root.InstalledAmazonGames(_logger), (root, install) => (Root: root, Install: install))
            .Where(entry => _registeredGames.Games.ContainsKey(entry.Install.ProductId))
            .GroupBy(entry => entry.Install.ProductId);

        foreach (var holders in installs)
        {
            var productId = holders.Key;
            var game = _registeredGames.Games[productId];

            // With the game in both Heroic installs nothing says which copy the user means, so locate
            // neither and say why.
            if (holders.Select(holder => holder.Root).Distinct().Count() > 1)
            {
                var message = $"Amazon game {productId} is installed under both Heroic config roots: " +
                              string.Join(" and ", holders.Select(holder => $"'{holder.Root.Path}' ({holder.Install.InstallPath})")) +
                              "; it is not located until it is removed from one";
                _logger.LogError("{Message}", message);
                problems?.Report(nameof(HeroicAmazonLocator), message);
                continue;
            }

            var (root, install) = holders.First();
            var settings = HeroicGameSettings.Read(root, productId, _logger);
            var compatibility = settings?.WinePrefix is { } winePrefix
                ? CompatibilityLayer(root, _fileSystem.FromUnsanitizedFullPath(winePrefix), settings)
                : null;

            yield return new GameLocatorResult
            {
                Game = game,
                Path = _fileSystem.FromUnsanitizedFullPath(install.InstallPath),
                LocatorIds = install.Version is null ? [] : [LocatorId.From(install.Version)],
                StoreIdentifier = productId,
                Store = Store,
                Locator = this,
                // Amazon Games ships Windows builds only, so there is no native case to tell apart.
                Platform = OSPlatform.Windows,
                WinePrefixPath = compatibility?.Prefix,
                Compatibility = compatibility,
            };
        }
    }

    private CompatibilityLayer CompatibilityLayer(HeroicConfigRoot root, AbsolutePath prefix, HeroicGameSettings settings)
    {
        var kind = root.IsFlatpak ? CompatibilityLayerKind.HeroicFlatpak : CompatibilityLayerKind.Heroic;
        return new CompatibilityLayer(kind, prefix, settings.WineBin is null ? null : _fileSystem.FromUnsanitizedFullPath(settings.WineBin), settings.WineName);
    }
}
