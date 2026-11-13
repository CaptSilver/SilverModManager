using FluentAssertions;
using GameFinder.Wine;
using NexusMods.Paths;
using SilverModManager.Games.Generic.Simple;

namespace SilverModManager.Games.Generic.Tests.Simple;

public class SimpleLocationsTests
{
    private static IFileSystem Overlay()
    {
        var root = FileSystem.Shared.GetKnownPath(KnownPath.TempDirectory).Combine("simple-locations-" + Guid.NewGuid().ToString("N"));
        var prefix = new WinePrefix { ConfigurationDirectory = root };
        return prefix.CreateOverlayFileSystem(FileSystem.Shared);
    }

    [Fact]
    public void LocalLowIsASiblingOfLocalAppData()
    {
        var overlay = Overlay();

        var localLow = SimpleLocations.Resolve(overlay, ModLocationRoot.LocalAppDataLow);

        localLow.Should().Be(overlay.GetKnownPath(KnownPath.LocalApplicationDataDirectory).Parent.Combine("LocalLow"));
        localLow.ToString().Should().EndWith("AppData/LocalLow");
    }

    [Fact]
    public void EveryRootOutsideTheGameFolderHasItsOwnLocationId()
    {
        Enum.GetValues<ModLocationRoot>().Where(root => root != ModLocationRoot.GameFolder)
            .Select(SimpleLocations.For).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TheGameFolderHasNoKnownFolder()
    {
        var overlay = Overlay();

        FluentActions.Invoking(() => SimpleLocations.Resolve(overlay, ModLocationRoot.GameFolder))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
