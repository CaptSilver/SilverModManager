using System.Diagnostics;
using GameFinder.Wine;
using GameFinder.Wine.Bottles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Backend.Games.Locators;

internal class WinePrefixWrappingLocator : IGameLocator
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;
    private readonly IFileSystem _fileSystem;
    private readonly ILoggerFactory _loggerFactory;

    private readonly LocatorFactory[] _locatorFactories;
    private readonly IWinePrefixManager<WinePrefix> _winePrefixManager;
    private readonly IWinePrefixManager<BottlesWinePrefix> _bottlesPrefixManager;

    public delegate IGameLocator LocatorFactory(IServiceProvider serviceProvider, ILoggerFactory loggerFactory, IFileSystem fileSystem, GameFinder.RegistryUtils.IRegistry registry);

    public WinePrefixWrappingLocator(IServiceProvider serviceProvider, LocatorFactory[] locatorFactories)
    {
        _serviceProvider = serviceProvider;
        _logger = serviceProvider.GetRequiredService<ILogger<WinePrefixWrappingLocator>>();
        _fileSystem = serviceProvider.GetRequiredService<IFileSystem>();
        _loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        _locatorFactories = locatorFactories;

        _winePrefixManager = new DefaultWinePrefixManager(_fileSystem);
        _bottlesPrefixManager = new BottlesWinePrefixManager(_fileSystem);
    }

    public IEnumerable<GameLocatorResult> Locate() => Locate(NoProblemSink.Instance);

    public IEnumerable<GameLocatorResult> Locate(IGameLocatorProblemSink problems)
    {
        var allPrefix = new List<AWinePrefix>();

        CollectPrefixes(_winePrefixManager, allPrefix);
        CollectPrefixes(_bottlesPrefixManager, allPrefix);

        var results = new List<GameLocatorResult>();

        foreach (var winePrefix in allPrefix)
        {
            var started = Stopwatch.GetTimestamp();
            var before = results.Count;
            var overlayFileSystem = winePrefix.CreateOverlayFileSystem(_fileSystem);
            // The registry files sit at the prefix root on the host; the overlay is for the C:\ paths the
            // wrapped locators read, and an in-memory overlay doesn't see host paths at all.
            var registry = winePrefix.CreateRegistry(_fileSystem);
            var prefixCompatibility = PrefixCompatibility(winePrefix);

            foreach (var factory in _locatorFactories)
            {
                try
                {
                    var locator = factory(_serviceProvider, _loggerFactory, overlayFileSystem, registry);
                    results.AddRange(locator.Locate(problems).Select(result => WithPrefix(result, winePrefix, prefixCompatibility)));
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Exception in locator created from factory");
                }
            }

            _logger.LogDebug("Scanned WINE prefix {Prefix} in {Elapsed} ms, found {Count} games", winePrefix.ConfigurationDirectory, Stopwatch.GetElapsedTime(started).TotalMilliseconds, results.Count - before);
        }

        return results;
    }

    private void CollectPrefixes<TPrefix>(IWinePrefixManager<TPrefix> manager, List<AWinePrefix> into) where TPrefix : AWinePrefix
    {
        foreach (var result in manager.FindPrefixes())
        {
            if (result.TryPickT1(out var errorMessage, out var winePrefix))
            {
                _logger.LogWarning("Error locating WINE prefix: {ErrorMessage}", errorMessage.Message);
                continue;
            }

            into.Add(winePrefix);
        }
    }

    /// <summary>
    /// Records the prefix a wrapped result was found in. A locator that already knows its own compatibility
    /// layer keeps it.
    /// </summary>
    private static GameLocatorResult WithPrefix(GameLocatorResult result, AWinePrefix winePrefix, CompatibilityLayer prefixCompatibility)
    {
        return new GameLocatorResult
        {
            Game = result.Game,
            Path = result.Path,
            Store = result.Store,
            StoreIdentifier = result.StoreIdentifier,
            LocatorIds = result.LocatorIds,
            Locator = result.Locator,
            Platform = result.Platform,
            LinuxCompatabilityDataProvider = result.LinuxCompatabilityDataProvider,
            StoreRoot = result.StoreRoot,
            StoreBuildId = result.StoreBuildId,
            StoreUpdatePending = result.StoreUpdatePending,
            StoreLastWrite = result.StoreLastWrite,
            WinePrefixPath = result.WinePrefixPath ?? winePrefix.ConfigurationDirectory,
            Compatibility = result.Compatibility ?? prefixCompatibility,
        };
    }

    private CompatibilityLayer PrefixCompatibility(AWinePrefix winePrefix) => winePrefix is BottlesWinePrefix bottle
        ? BottleCompatibility(bottle)
        : new CompatibilityLayer(CompatibilityLayerKind.PlainWine, winePrefix.ConfigurationDirectory);

    /// <summary>
    /// A bottle names its runner in <c>bottle.yml</c>; Bottles unpacks runners next to the <c>bottles</c>
    /// folder, in <c>runners/&lt;Runner&gt;/</c>. A system Wine runner has no such folder, so the binary is
    /// left unset rather than guessed.
    /// </summary>
    private CompatibilityLayer BottleCompatibility(BottlesWinePrefix bottle)
    {
        var prefix = bottle.ConfigurationDirectory;
        string? runner;
        try
        {
            runner = ReadRunnerName(bottle.GetBottlesConfigFile());
        }
        catch (IOException e)
        {
            _logger.LogWarning(e, "Unable to read the runner of bottle {Bottle}", prefix);
            runner = null;
        }

        if (runner is null) return new CompatibilityLayer(CompatibilityLayerKind.PlainWine, prefix);

        var wine = prefix.Parent.Parent.Combine("runners").Combine(runner).Combine("bin/wine");
        return new CompatibilityLayer(
            CompatibilityLayerKind.PlainWine,
            prefix,
            wine.FileExists ? wine : null,
            runner
        );
    }

    private static string? ReadRunnerName(AbsolutePath config)
    {
        if (!config.FileExists) return null;

        // Only the one top-level scalar is needed, so a line scan avoids a YAML dependency.
        using var reader = new StreamReader(config.Read());
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith("Runner:", StringComparison.Ordinal)) continue;

            var value = line["Runner:".Length..].Trim().Trim('\'', '"');
            return value.Length == 0 || value.Contains('/') || value.Contains("..") ? null : value;
        }

        return null;
    }

    private sealed class NoProblemSink : IGameLocatorProblemSink
    {
        public static readonly NoProblemSink Instance = new();

        public void Report(string locator, string message) { }
    }
}
