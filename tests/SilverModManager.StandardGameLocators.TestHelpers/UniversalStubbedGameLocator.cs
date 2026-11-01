using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.StandardGameLocators.TestHelpers;

/// <summary>
/// Locates one game in a throwaway folder. The game comes from a factory or a <see cref="GameId"/>, so a game that is
/// an instance built from a definition can be located as well as one the container can resolve by its type.
/// </summary>
public class UniversalStubbedGameLocator : IGameLocator, IDisposable
{
    private readonly TemporaryPath _path;

    /// <summary>
    /// The game folder as seen through the file system that resolves the game's known folders. Game
    /// locations are computed from <c>Path.FileSystem</c>, so passing a Wine prefix overlay here makes a
    /// game's Documents and AppData locations land in the prefix, as for a game installed through Wine.
    /// </summary>
    private readonly AbsolutePath _locatedPath;
    private readonly IGame _game;
    private readonly GameStore[] _stores;

    public LocatorId[] LocatorIds { get; set; } = [LocatorId.From("StubbedGameState.zip")];

    /// <summary>
    /// The platform reported for the located game. Set it together with <see cref="WinePrefixPath"/> to
    /// stand in for a Windows build running under Proton.
    /// </summary>
    public OSPlatform Platform { get; set; } = OSPlatform.Linux;

    /// <summary>
    /// The prefix directory reported for the located game, or null for a native Linux build. It does not
    /// have to exist: a game that was never launched has no prefix yet.
    /// </summary>
    public AbsolutePath? WinePrefixPath { get; set; }

    /// <summary>
    /// The compatibility data reported for the located game, or null for one without a prefix to read.
    /// </summary>
    public ILinuxCompatabilityDataProvider? LinuxCompatabilityDataProvider { get; set; }

    /// <param name="gameFactory">Builds or finds the game this locator reports.</param>
    /// <param name="folderName">Names the game folder, so two games located in one test do not share it.</param>
    public UniversalStubbedGameLocator(
        IServiceProvider serviceProvider,
        IFileSystem fileSystem,
        TemporaryFileManager fileManager,
        Func<IServiceProvider, IGame> gameFactory,
        string folderName,
        Dictionary<RelativePath, byte[]>? gameFiles = null,
        GameStore[]? stores = null,
        IFileSystem? locationsFileSystem = null)
    {
        _stores = stores ?? [GameStore.Unknown];
        _path = fileManager.CreateFolder(folderName);
        _locatedPath = locationsFileSystem is null
            ? _path.Path
            : locationsFileSystem.FromUnsanitizedFullPath(_path.Path.GetFullPath());
        _game = gameFactory(serviceProvider);

        if (gameFiles is null) return;
        foreach (var gameFile in gameFiles)
        {
            var gameFilePath = _path.Path.Combine(gameFile.Key);
            gameFilePath.Parent.CreateDirectory();
            using var stream = fileSystem.CreateFile(gameFilePath);
            stream.Write(gameFile.Value, 0, gameFile.Value.Length);
        }
    }

    /// <summary>Locates the registered <see cref="IGame"/> with this id, in a folder named after the id.</summary>
    public UniversalStubbedGameLocator(
        IServiceProvider serviceProvider,
        IFileSystem fileSystem,
        TemporaryFileManager fileManager,
        GameId gameId,
        Dictionary<RelativePath, byte[]>? gameFiles = null,
        GameStore[]? stores = null,
        IFileSystem? locationsFileSystem = null)
        : this(serviceProvider, fileSystem, fileManager, provider => FindGame(provider, gameId), gameId.ToString(), gameFiles, stores, locationsFileSystem)
    {
    }

    private static IGame FindGame(IServiceProvider provider, GameId gameId) =>
        provider.GetServices<IGame>().FirstOrDefault(game => game.GameId == gameId)
        ?? throw new InvalidOperationException($"No registered game has the id '{gameId}', so the stubbed locator has nothing to locate.");

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
                Platform = Platform,
                WinePrefixPath = WinePrefixPath,
                LinuxCompatabilityDataProvider = LinuxCompatabilityDataProvider,
            };
        }
    }

    public void Dispose()
    {
        _path.Dispose();
    }
}

/// <summary>Locates the game the container resolves as <typeparamref name="TGame"/>.</summary>
public class UniversalStubbedGameLocator<TGame>(
    IServiceProvider serviceProvider,
    IFileSystem fileSystem,
    TemporaryFileManager fileManager,
    Dictionary<RelativePath, byte[]>? gameFiles = null,
    GameStore[]? stores = null,
    IFileSystem? locationsFileSystem = null)
    : UniversalStubbedGameLocator(
        serviceProvider,
        fileSystem,
        fileManager,
        provider => provider.GetRequiredService<TGame>(),
        typeof(TGame).Name,
        gameFiles,
        stores,
        locationsFileSystem)
    where TGame : IGame;
