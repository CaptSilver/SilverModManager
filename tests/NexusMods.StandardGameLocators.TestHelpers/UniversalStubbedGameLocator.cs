using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Games;
using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.StandardGameLocators.TestHelpers;

public class UniversalStubbedGameLocator<TGame> : IGameLocator, IDisposable
    where TGame : IGame
{
    private readonly TemporaryPath _path;

    /// <summary>
    /// The game folder as seen through the file system that resolves the game's known folders. Game
    /// locations are computed from <c>Path.FileSystem</c>, so passing a Wine prefix overlay here makes a
    /// game's Documents and AppData locations land in the prefix, as for a game installed through Wine.
    /// </summary>
    private readonly AbsolutePath _locatedPath;
    private readonly TGame _game;
    private readonly GameStore[] _stores;

    public LocatorId[] LocatorIds { get; set; } = [LocatorId.From("StubbedGameState.zip")];

    public UniversalStubbedGameLocator(
        IServiceProvider serviceProvider,
        IFileSystem fileSystem,
        TemporaryFileManager fileManager,
        Dictionary<RelativePath, byte[]>? gameFiles = null,
        GameStore[]? stores = null,
        IFileSystem? locationsFileSystem = null)
    {
        _stores = stores ?? [GameStore.Unknown];
        _path = fileManager.CreateFolder(typeof(TGame).Name);
        _locatedPath = locationsFileSystem is null
            ? _path.Path
            : locationsFileSystem.FromUnsanitizedFullPath(_path.Path.GetFullPath());
        _game = serviceProvider.GetRequiredService<TGame>();

        if (gameFiles is null) return;
        foreach (var gameFile in gameFiles)
        {
            var gameFilePath = _path.Path.Combine(gameFile.Key);
            using var stream = fileSystem.CreateFile(gameFilePath);
            stream.Write(gameFile.Value, 0, gameFile.Value.Length);
        }
    }

    public IEnumerable<GameLocatorResult> Locate()
    {
        foreach (var store in _stores)
        {
            yield return new GameLocatorResult
            {
                Game = _game,
                Locator = this,
                LocatorIds = [..LocatorIds],
                Store = store,
                Path = _locatedPath,
                StoreIdentifier = LocatorIds[0].Value,
            };
        }
    }

    public void Dispose()
    {
        _path.Dispose();
    }
}
