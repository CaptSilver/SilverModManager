using System.Collections.Immutable;
using FluentAssertions;
using SilverModManager.Abstractions.Loadouts.Deployment;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.Doctor;

public class DoctorDeploymentRootTests
{
    private static readonly IReadOnlySet<DeploymentBackendId> OnlyCopy = new HashSet<DeploymentBackendId> { DeploymentBackendId.Copy };

    private static GameLocations Locations(params LocationId[] ids)
    {
        var fs = new InMemoryFileSystem(OSInformation.FakeUnix);
        var map = ImmutableDictionary<LocationId, AbsolutePath>.Empty;
        foreach (var id in ids) map = map.Add(id, fs.FromUnsanitizedFullPath($"/games/{id}"));
        return GameLocations.Create(map);
    }

    [Fact]
    public void From_NoRecordedRows_ListsEveryLocationAsAnImpliedCopy_InNameOrder()
    {
        var roots = DoctorDeploymentRoot.From(DeploymentPlan.Empty, Locations(LocationId.Saves, LocationId.Preferences, LocationId.Game), OnlyCopy);

        roots.Should().Equal(
            new DoctorDeploymentRoot("Game", "copy", Recorded: false, Registered: true),
            new DoctorDeploymentRoot("Preferences", "copy", Recorded: false, Registered: true),
            new DoctorDeploymentRoot("Saves", "copy", Recorded: false, Registered: true));
    }

    [Fact]
    public void From_RecordedRowForAnUnregisteredBackend_IsListedAsRecordedAndUnregistered()
    {
        var recorded = new DeploymentPlan([new DeploymentPlanRoot(new GamePath(LocationId.Game, "mods"), new DeploymentBackendId("x"))]);

        var roots = DoctorDeploymentRoot.From(recorded, Locations(LocationId.Game), OnlyCopy);

        roots.Should().BeEquivalentTo(new[]
        {
            new DoctorDeploymentRoot("Game/mods", "x", Recorded: true, Registered: false),
            new DoctorDeploymentRoot("Game", "copy", Recorded: false, Registered: true),
        }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void From_CompleteRecordedPlan_AddsNothing()
    {
        var locations = Locations(LocationId.Game);
        var recorded = DeploymentPlan.Default(locations);

        DoctorDeploymentRoot.From(recorded, locations, OnlyCopy)
            .Should().Equal(new DoctorDeploymentRoot("Game", "copy", Recorded: true, Registered: true));
    }

    [Fact]
    public void From_CarriesChosenAndReasonOfRecordedRows()
    {
        var recorded = new DeploymentPlan([
            new DeploymentPlanRoot(new GamePath(LocationId.Game, RelativePath.Empty), DeploymentBackendId.Hardlink, Chosen: true, Reason: "picked by hand"),
        ]);

        DoctorDeploymentRoot.From(recorded, Locations(LocationId.Game), OnlyCopy)
            .Should().Equal(new DoctorDeploymentRoot("Game", "hardlink", Recorded: true, Registered: false, Chosen: true, Reason: "picked by hand"));
    }

    [Fact]
    public void From_AvailableIsSetOnlyForChosenRowsTheChainWouldMove()
    {
        var game = new GamePath(LocationId.Game, RelativePath.Empty);
        var saves = new GamePath(LocationId.Saves, RelativePath.Empty);
        var prefs = new GamePath(LocationId.Preferences, RelativePath.Empty);
        var recorded = new DeploymentPlan([
            new DeploymentPlanRoot(game, DeploymentBackendId.Copy, Chosen: true, Reason: null),
            new DeploymentPlanRoot(saves, DeploymentBackendId.Copy, Chosen: false, Reason: "default"),
            new DeploymentPlanRoot(prefs, DeploymentBackendId.Hardlink, Chosen: true, Reason: null),
        ]);
        var asked = new List<GamePath>();

        var roots = DoctorDeploymentRoot.From(recorded, Locations(LocationId.Game, LocationId.Saves, LocationId.Preferences, LocationId.AppData), OnlyCopy,
            available: root => { asked.Add(root); return DeploymentBackendId.Hardlink; });

        roots.Single(r => r.Root == "Game").Available.Should().Be("hardlink");
        roots.Single(r => r.Root == "Saves").Available.Should().BeNull("the next apply moves an unchosen row by itself");
        roots.Single(r => r.Root == "Preferences").Available.Should().BeNull("the chain agrees with the row");
        roots.Single(r => r.Root == "AppData").Available.Should().BeNull("an unrecorded root is not a choice either");
        asked.Should().BeEquivalentTo([game, prefs], "the chain probes the disk, so it is asked only about chosen rows");
    }
}
