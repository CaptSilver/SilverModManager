using DynamicData.Kernel;
using FluentAssertions;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Me3;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.Games.FromSoft.Tests;

public class Me3LoaderTests(ITestOutputHelper helper) : AEldenRingTest<Me3LoaderTests>(helper)
{
    private static readonly IFileSystem HostFileSystem = NexusMods.Paths.FileSystem.Shared.CreateOverlayFileSystem(
        new Dictionary<AbsolutePath, AbsolutePath>(),
        new Dictionary<KnownPath, AbsolutePath> { [KnownPath.XDG_DATA_HOME] = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/fake/data") });

    private static FromSoftSettings DefaultSettings => FromSoftSettings.CreateDefault(null!);

    [Fact]
    public void AGameThatIsNotAFromSoftGameIsNotServed()
    {
        var other = new IdentifiedStubbedGame(GameId.From("Other.Game"), Optional<Sdk.NexusModsApi.NexusModsGameId>.None);

        Me3Loader.Serves(other).Should().BeFalse();
    }

    [Fact]
    public void EldenRingIsServed()
    {
        Me3Loader.Serves(GameInstallation.Game).Should().BeTrue();
    }

    [Fact]
    public void TheModsAndHostFoldersAreInTheInstallationRootOfTheLayout()
    {
        var root = $"/fake/data/{ApplicationConstants.DataDirectoryName}/FromSoft/TestSouls/424242";

        Me3Loader.ModsFolder(TestSoulsDefinition.Layout, DefaultSettings, HostFileSystem, "424242").ToString().Should().Be($"{root}/mods");
        Me3Loader.HostFolder(TestSoulsDefinition.Layout, DefaultSettings, HostFileSystem, "424242").ToString().Should().Be($"{root}/me3");
    }

    [Fact]
    public void TheLoaderOffersTheModsAndHostLocations()
    {
        var loader = new Me3Loader(ServiceProvider, new Me3LoaderOptions("testsouls", []), TestSoulsDefinition.Definition);

        var locations = loader.Locations(DefaultSettings, HostFileSystem, "424242");

        locations.Keys.Should().BeEquivalentTo([LocationId.Me3Mods, LocationId.Me3Host]);
        locations[LocationId.Me3Mods].Should().Be(Me3Loader.ModsFolder(TestSoulsDefinition.Layout, DefaultSettings, HostFileSystem, "424242"));
        locations[LocationId.Me3Host].Should().Be(Me3Loader.HostFolder(TestSoulsDefinition.Layout, DefaultSettings, HostFileSystem, "424242"));
    }

    [Theory]
    [InlineData("me3 0.7.0")]
    [InlineData("me3 v0.7.9")]
    public void AnMe3BelowTheFloorIsRefusedNamingTheFloor(string output)
    {
        var veto = Me3Loader.VersionVeto(output, "Elden Ring", "Main", Substitute.For<ILogger>());

        veto!.Message.Should().Contain("0.8.1").And.Contain("Update me3").And.Contain("'Main'");
    }

    [Theory]
    [InlineData("me3 0.8.1")]
    [InlineData("me3 v0.8.1")]
    [InlineData("me3 0.13.0")] // printed by the me3 0.13.0 release deployed in a library
    [InlineData("me3-cli 0.9.0-rc.1")]
    public void AnMe3AtOrAboveTheFloorPasses(string output)
    {
        Me3Loader.VersionVeto(output, "Elden Ring", "Main", Substitute.For<ILogger>()).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n")]
    public void NoOutputIsNotAVeto(string? output)
    {
        Me3Loader.VersionVeto(output, "Elden Ring", "Main", Substitute.For<ILogger>()).Should().BeNull();
    }

    [Fact]
    public void UnparseableOutputIsLoggedAndLetThrough()
    {
        var logger = Substitute.For<ILogger>();

        Me3Loader.VersionVeto("garbage", "Elden Ring", "Main", logger).Should().BeNull();

        logger.ReceivedCalls().Should().Contain(call => call.GetMethodInfo().Name == nameof(ILogger.Log) && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning);
    }
}
