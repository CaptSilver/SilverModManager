using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Games.Locators;

/// <summary>
/// Turns the installs the user recorded into game installations. Store clients never report these, so the
/// record carries what a locator would otherwise detect: the platform and, for a Windows build, the prefix.
/// </summary>
internal sealed class UserInstallLocator : IGameLocator
{
    private readonly ILogger<UserInstallLocator> _logger;
    private readonly IUserGameInstallStore _store;
    private readonly IFileSystem _fileSystem;
    private readonly IEnumerable<IGameData> _games;

    public UserInstallLocator(ILogger<UserInstallLocator> logger, IUserGameInstallStore store, IFileSystem fileSystem, IEnumerable<IGameData> games)
    {
        _logger = logger;
        _store = store;
        _fileSystem = fileSystem;
        _games = games;
    }

    // A record that can't be used is skipped, never thrown on: the registry drops a locator that throws,
    // which would hide every other install along with it.
    public IEnumerable<GameLocatorResult> Locate()
    {
        foreach (var install in _store.GetAll())
        {
            var game = _games.FirstOrDefault(data => data.GameId == install.Game);
            if (game is null)
            {
                _logger.LogWarning("Ignoring the recorded install at {Path}: no game with id {GameId} is known", install.Path, install.Game);
                continue;
            }

            var path = _fileSystem.FromUnsanitizedFullPath(install.Path);
            if (!path.DirectoryExists())
            {
                _logger.LogWarning("Ignoring the recorded {Game} install at {Path}: the folder does not exist (an unplugged drive?)", game.DisplayName, path);
                continue;
            }

            AbsolutePath? prefix = install.Prefix is { } recorded ? _fileSystem.FromUnsanitizedFullPath(recorded) : null;
            AbsolutePath? wine = install.Wine is { } binary ? _fileSystem.FromUnsanitizedFullPath(binary) : null;

            yield return new GameLocatorResult
            {
                Game = game,
                Path = path,
                Store = GameStore.ManuallyAdded,
                StoreIdentifier = install.Path,
                LocatorIds = [],
                Locator = this,
                Platform = install.Platform,
                WinePrefixPath = prefix,
                Compatibility = prefix is { } layerPrefix ? new CompatibilityLayer(CompatibilityLayerKind.PlainWine, layerPrefix, wine) : null,
            };
        }
    }
}
