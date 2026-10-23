using System.Collections.Immutable;
using FluentAssertions;
using GameFinder.Wine;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.TestFramework.Helpers;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Games.MountAndBlade2Bannerlord.Tests;

public class BannerlordLocationTests(IServiceProvider serviceProvider)
{
    /// <summary>
    /// A plain Unix file system has no Documents folder, so Bannerlord's locations only resolve
    /// through a Wine prefix overlay, which maps the Windows known folders into the prefix.
    /// </summary>
    [Fact]
    public void GetLocations_PutsSavesUnderTheDocumentsFolder()
    {
        var game = serviceProvider.GetServices<IGame>().OfType<Bannerlord>().Single();
        var host = new InMemoryFileSystem(OSInformation.FakeUnix);
        var prefix = new WinePrefix { ConfigurationDirectory = host.FromUnsanitizedFullPath("/prefix") };
        var fileSystem = prefix.CreateOverlayFileSystem(host);
        var documents = fileSystem.GetKnownPath(KnownPath.MyDocumentsDirectory);

        var locations = game.GetLocations(fileSystem, LocatorResult(fileSystem));

        locations[LocationId.Saves].Should()
            .Be(documents.Combine($"{BannerlordConstants.DocumentsFolderName}/Game Saves"));
        locations[LocationId.Saves].GetFullPath().Should().StartWith("/prefix/drive_c/users/");
    }

    [Fact]
    public void GetLocations_UnderProton_ResolvesSavesAndConfigsIntoThePrefix()
    {
        var prefix = ProtonTestPrefix.Create();
        var game = serviceProvider.GetServices<IGame>().OfType<Bannerlord>().Single();
        var documents = prefix.Home.Combine($"Documents/{BannerlordConstants.DocumentsFolderName}");

        var locations = game.GetLocations(prefix.GameFileSystem, prefix.LocatorResult());

        locations[LocationId.Saves].Should().Be(documents.Combine("Game Saves"));
        locations[LocationId.Preferences].Should().Be(documents.Combine("Configs"));
    }

    private static GameLocatorResult LocatorResult(IFileSystem fileSystem) => new()
    {
        Game = Substitute.For<IGameData>(),
        Path = fileSystem.FromUnsanitizedFullPath("/games/Bannerlord"),
        Store = GameStore.Steam,
        StoreIdentifier = "261550",
        LocatorIds = ImmutableArray<LocatorId>.Empty,
        Locator = Substitute.For<IGameLocator>(),
    };
}
