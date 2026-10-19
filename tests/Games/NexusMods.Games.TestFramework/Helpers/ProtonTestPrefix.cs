using System.Collections.Immutable;
using GameFinder.StoreHandlers.Steam;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NSubstitute;

namespace NexusMods.Games.TestFramework.Helpers;

/// <summary>
/// A Proton prefix under a fake Steam library, for tests that check where a game's known
/// folders land without touching a real disk.
/// </summary>
public sealed class ProtonTestPrefix
{
    /// <summary>
    /// The file system as the game sees it, with the Windows known folders mapped into the prefix.
    /// </summary>
    public IFileSystem GameFileSystem { get; }

    /// <summary>
    /// The prefix's <c>drive_c/users/steamuser</c> directory, the Windows home of a Proton game.
    /// </summary>
    public AbsolutePath Home { get; }

    private ProtonTestPrefix(IFileSystem gameFileSystem, AbsolutePath home)
    {
        GameFileSystem = gameFileSystem;
        Home = home;
    }

    public static ProtonTestPrefix Create(uint appId = 1)
    {
        var host = new InMemoryFileSystem(OSInformation.FakeUnix);
        var protonDirectory = host.FromUnsanitizedFullPath($"/steam/steamapps/compatdata/{appId}");
        var prefix = new ProtonWinePrefix { ProtonDirectory = protonDirectory, ConfigurationDirectory = protonDirectory.Combine("pfx") };
        var gameFileSystem = prefix.CreateOverlayFileSystem(host);
        return new ProtonTestPrefix(gameFileSystem, protonDirectory.Combine("pfx/drive_c/users/steamuser"));
    }

    /// <summary>
    /// A locator result for a game installed at <paramref name="gamePath"/> on <see cref="GameFileSystem"/>.
    /// </summary>
    public GameLocatorResult LocatorResult(GameStore? store = null, string gamePath = "/steam/steamapps/common/Game") => new()
    {
        Game = Substitute.For<IGameData>(),
        Path = GameFileSystem.FromUnsanitizedFullPath(gamePath),
        Store = store ?? GameStore.Steam,
        StoreIdentifier = "1",
        LocatorIds = ImmutableArray<LocatorId>.Empty,
        Locator = Substitute.For<IGameLocator>(),
        WinePrefixPath = Home.Parent.Parent.Parent,
    };
}
