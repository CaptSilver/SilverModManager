using FluentAssertions;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Deployment.Links.Tests.Fixtures;
using NexusMods.Sdk.Games;

namespace NexusMods.Deployment.Links.Tests;

/// <summary>
/// The selection chain with the real backends: where the game and the staging store sit decides the
/// backend, and every backend has to leave the folders exactly as a copy deploy does.
/// </summary>
public class LinkedStubbedGameTests(ITestOutputHelper helper)
{
    private static DeploymentBackendId BackendOf(DeploymentPlan plan, LocationId location) => plan.Roots.Single(r => r.Root.LocationId == location && r.Root.Path.Path.Length == 0).Backend;

    [Theory]
    [InlineData(Placement.TempDirectory, Placement.TempDirectory, "hardlink")]
    [InlineData(Placement.EntryDirectory, Placement.EntryDirectory, "reflink")]
    [InlineData(Placement.SharedMemory, Placement.TempDirectory, "symlink")]
    public async Task ApplyDisableAndResetThroughTheChainMatchTheCopyDeploy(Placement game, Placement store, string expectedBackend)
    {
        var copy = await DeployScenario.Run(helper, game, store, DeploymentBackendId.Copy);
        var chain = await DeployScenario.Run(helper, game, store, pinned: null);

        BackendOf(copy.Plan, LocationId.Game).Should().Be(DeploymentBackendId.Copy);
        BackendOf(chain.Plan, LocationId.Game).Value.Should().Be(expectedBackend);
        BackendOf(chain.Plan, LocationId.Saves).Should().Be(DeploymentBackendId.Copy);
        chain.Snapshot.Should().Be(copy.Snapshot);
    }

    [Fact]
    public async Task PlanRowsSayHardlinkWithTheReflinkRefusalInTheReason()
    {
        var chain = await DeployScenario.Run(helper, Placement.TempDirectory, Placement.TempDirectory, pinned: null);

        var row = chain.Plan.Roots.Single(r => r.Root.LocationId == LocationId.Game);
        row.Backend.Should().Be(DeploymentBackendId.Hardlink);
        row.Chosen.Should().BeFalse();
        row.Reason.Should().Contain("reflink").And.Contain("EOPNOTSUPP");
        row.Reason.Should().Contain("hardlink");
    }

    [Fact]
    public async Task ASymlinkRootWhenTheGameDirIsOnAnotherDevice()
    {
        var copy = await DeployScenario.Run(helper, Placement.SharedMemory, Placement.TempDirectory, DeploymentBackendId.Copy);
        var chain = await DeployScenario.Run(helper, Placement.SharedMemory, Placement.TempDirectory, pinned: null);

        BackendOf(chain.Plan, LocationId.Game).Should().Be(DeploymentBackendId.Symlink);
        chain.Plan.Roots.Single(r => r.Root.LocationId == LocationId.Game).Reason.Should().Contain("hardlink").And.Contain("EXDEV");
        chain.Symlinked.Should().NotBeEmpty("every hash but its private copies is a symlink into the store");
        copy.Symlinked.Should().BeEmpty();
        chain.Snapshot.Should().Be(copy.Snapshot);
    }
}
