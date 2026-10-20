using System.Collections.Immutable;
using FluentAssertions;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Doctor;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton.Tests.Doctor;

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
}
