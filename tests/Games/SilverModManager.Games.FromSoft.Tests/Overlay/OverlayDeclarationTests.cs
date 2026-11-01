using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.FromSoft.Tests.Overlay;

/// <summary>
/// <see cref="IGameData.DeploysThroughOverlay"/> is a claim, so these tests hold it to the behaviour it names: the
/// run tool's spec for a loadout with mods exists only inside the launch, and a sync leaves the real game folder
/// untouched. Changing either breaks the declaration, and with it the overlay rule over every registered game.
/// </summary>
public class EldenRingOverlayDeclarationTests(ITestOutputHelper helper) : AModdedEldenRingTest<EldenRingOverlayDeclarationTests>(helper)
{
    [Fact]
    public async Task TheDeclaredOverlayIsWhatTheModdedLaunchSpecDoes()
    {
        ((IGameData)Game).DeploysThroughOverlay.Should().BeTrue();
        var loadout = await CreateModdedLoadout();

        var spec = await ServiceProvider.GetServices<ITool>().OfType<RunEldenRingGame>().Single().GetLaunchSpec(loadout, CancellationToken.None);

        spec.FilesOnlyInLaunch.Should().BeTrue("a game that declares an overlay shows its mods only inside the launched process");
    }

    [Fact]
    public async Task TheDeclaredOverlayMeansASyncWritesNothingUnderTheRealGameFolder()
    {
        ((IGameData)Game).DeploysThroughOverlay.Should().BeTrue();
        var before = SnapshotOfGameFolder();

        await CreateModdedLoadout();

        SnapshotOfGameFolder().Should().Equal(before);
    }
}

/// <summary>The same two claims for a FromSoft game that shares none of Elden Ring's definition.</summary>
public class TestSoulsOverlayDeclarationTests(ITestOutputHelper helper) : AModdedTestSoulsTest<TestSoulsOverlayDeclarationTests>(helper)
{
    [Fact]
    public async Task TheDeclaredOverlayIsWhatTheModdedLaunchSpecDoes()
    {
        ((IGameData)Game).DeploysThroughOverlay.Should().BeTrue();
        var loadout = await CreateModdedLoadout();

        var spec = await ServiceProvider.GetServices<ITool>().OfType<RunFromSoftGame<TestSoulsGame>>().Single().GetLaunchSpec(loadout, CancellationToken.None);

        spec.FilesOnlyInLaunch.Should().BeTrue("a game that declares an overlay shows its mods only inside the launched process");
    }

    [Fact]
    public async Task TheDeclaredOverlayMeansASyncWritesNothingUnderTheRealGameFolder()
    {
        ((IGameData)Game).DeploysThroughOverlay.Should().BeTrue();
        var before = TreeSnapshot.Take(RealGameFolder);

        await CreateModdedLoadout();

        TreeSnapshot.Take(RealGameFolder).Should().Equal(before);
    }
}
