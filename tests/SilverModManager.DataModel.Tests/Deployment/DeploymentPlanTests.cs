using FluentAssertions;
using System.Collections.Immutable;
using SilverModManager.Abstractions.Loadouts.Deployment;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.DataModel.Tests.Deployment;

public class DeploymentPlanTests
{
    private static readonly DeploymentBackendId Other = new("other");

    private static GamePath Game(string path) => new(LocationId.Game, RelativePath.FromUnsanitizedInput(path));

    private static DeploymentPlan NestedPlan() => new(
    [
        new DeploymentPlanRoot(Game(""), DeploymentBackendId.Copy),
        new DeploymentPlanRoot(Game("mods"), Other),
        new DeploymentPlanRoot(Game("mods/deep"), DeploymentBackendId.Copy),
    ]);

    [Theory]
    [InlineData("mods/a.txt", "other")]
    [InlineData("mods/deep/c.txt", "copy")]
    [InlineData("other/b.txt", "copy")]
    [InlineData("modsX/a.txt", "copy")]
    [InlineData("mods", "other")]
    // GamePath equality ignores case, so routing must too or one sync node could straddle two backends.
    [InlineData("MODS/a.txt", "other")]
    public void BackendFor_PicksLongestSegmentPrefix(string path, string expected)
    {
        NestedPlan().BackendFor(Game(path)).Value.Should().Be(expected);
    }

    [Fact]
    public void BackendFor_IgnoresRootsOfOtherLocations()
    {
        var plan = new DeploymentPlan([new DeploymentPlanRoot(Game(""), Other)]);

        plan.BackendFor(new GamePath(LocationId.Saves, RelativePath.FromUnsanitizedInput("a.sav")))
            .Should().Be(DeploymentBackendId.Copy);
        plan.IsComplete(Locations(LocationId.Game, LocationId.Saves)).Should().BeFalse();
    }

    [Fact]
    public void Default_RoutesEveryLocationToCopy()
    {
        var locations = Locations(LocationId.Game, LocationId.Preferences, LocationId.Saves);
        var plan = DeploymentPlan.Default(locations);

        plan.Roots.Should().HaveCount(3);
        plan.Roots.Select(r => r.Backend).Should().OnlyContain(b => b == DeploymentBackendId.Copy);
        plan.Roots.Select(r => r.Root.LocationId).Should().BeEquivalentTo([LocationId.Game, LocationId.Preferences, LocationId.Saves]);
        plan.IsComplete(locations).Should().BeTrue();
        plan.MissingRoots(locations).Should().BeEmpty();
    }

    [Fact]
    public void Default_CoversNestedLocations()
    {
        var locations = GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty
            .Add(LocationId.Game, FileSystem.Shared.FromUnsanitizedFullPath("/fixture/game"))
            .Add(LocationId.From("Data"), FileSystem.Shared.FromUnsanitizedFullPath("/fixture/game/Data")));

        DeploymentPlan.Default(locations).IsComplete(locations).Should().BeTrue();
        DeploymentPlan.Default(locations).Roots.Should().HaveCount(2);
    }

    [Fact]
    public void MissingRoots_ListsExactlyTheUncoveredLocations()
    {
        var locations = Locations(LocationId.Game, LocationId.Preferences, LocationId.Saves);
        var plan = new DeploymentPlan(
        [
            new DeploymentPlanRoot(Game(""), DeploymentBackendId.Copy),
            // A sub-folder root does not cover its location.
            new DeploymentPlanRoot(new GamePath(LocationId.Saves, RelativePath.FromUnsanitizedInput("slots")), Other),
        ]);

        plan.IsComplete(locations).Should().BeFalse();
        plan.MissingRoots(locations).Should().BeEquivalentTo(
        [
            new GamePath(LocationId.Preferences, RelativePath.Empty),
            new GamePath(LocationId.Saves, RelativePath.Empty),
        ]);
    }

    [Fact]
    public void Empty_RoutesToCopyAndIsIncomplete()
    {
        DeploymentPlan.Empty.BackendFor(Game("a.txt")).Should().Be(DeploymentBackendId.Copy);
        DeploymentPlan.Empty.IsComplete(Locations(LocationId.Game)).Should().BeFalse();
    }

    [Fact]
    public void BackendId_ToStringIsValue()
    {
        DeploymentBackendId.Copy.ToString().Should().Be("copy");
    }

    private static GameLocations Locations(params LocationId[] ids)
    {
        var map = ImmutableDictionary<LocationId, AbsolutePath>.Empty;
        foreach (var id in ids) map = map.Add(id, FileSystem.Shared.FromUnsanitizedFullPath($"/fixture/{id}"));
        return GameLocations.Create(map);
    }
}
