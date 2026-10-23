using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Backend.Games.Locators;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Backend.Tests.Games;

/// <summary>
/// Runs the Steam locator over the machine's real Steam library. Excluded from the default run
/// because the outcome depends on what happens to be installed.
/// </summary>
public class SteamLocatorHostTests
{
    private static readonly (string Name, uint AppId)[] RegisteredGames =
    [
        ("Skyrim Special Edition", 489830),
        ("Fallout 4", 377160),
        ("Baldur's Gate 3", 1086940),
        ("Cyberpunk 2077", 1091500),
        ("Mount & Blade II: Bannerlord", 261550),
        ("Stardew Valley", 413150),
    ];

    [Fact]
    [Trait("RequiresProton", "True")]
    public void Locate_InstalledProtonGames_ResolveKnownFoldersInsideAnExistingPrefix()
    {
        var games = RegisteredGames.Select((entry, index) =>
        {
            var game = Substitute.For<IGameData>();
            game.StoreIdentifiers.Returns(new StoreIdentifiers(GameId.From((uint)index + 1)) { SteamAppIds = [entry.AppId] });
            return game;
        }).ToArray();

        var locator = new SteamLocator(games, NullLoggerFactory.Instance, FileSystem.Shared, registry: null);
        var withProvider = locator.Locate().Where(result => result.LinuxCompatabilityDataProvider is not null).ToArray();

        Assert.SkipWhen(withProvider.Length == 0, "No registered game with a Proton prefix is installed in a Steam library on this machine");

        foreach (var result in withProvider)
        {
            var prefix = result.LinuxCompatabilityDataProvider!.WinePrefixDirectoryPath;
            prefix.DirectoryExists().Should().BeTrue($"{result.Game.StoreIdentifiers.SteamAppIds[0]} has a ready prefix");
            ProtonPrefix.IsReady(prefix).Should().BeTrue();
            result.WinePrefixPath.Should().Be(prefix);

            var localAppData = result.Path.FileSystem.GetKnownPath(KnownPath.LocalApplicationDataDirectory);
            localAppData.GetFullPath().Should().StartWith(prefix.GetFullPath());
        }
    }
}
