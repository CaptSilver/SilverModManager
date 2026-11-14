using System.Collections.Immutable;
using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Deployment.Fuse.Availability;
using SilverModManager.Deployment.Fuse.Backend;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Deployment.Fuse.Tests.Backend;

public sealed class FusePreflightTests : IDisposable
{
    private static readonly GamePath Data = new(LocationId.Game, "Data");
    private static readonly FuseAvailabilityReport Usable = new(true, null, []);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-preflight-" + Guid.NewGuid().ToString("N"));
    private AbsolutePath GameFolder => FileSystem.Shared.FromUnsanitizedFullPath(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private GameInstallation Installation(bool mountable = true, bool timestamps = false, string folderName = "Data")
    {
        Directory.CreateDirectory(Path.Combine(_root, folderName));
        var interfaces = new List<Type> { typeof(IGameData) };
        if (mountable) interfaces.Add(typeof(IMountableGame));
        if (timestamps) interfaces.Add(typeof(ITimestampOrderedGame));
        var game = (IGameData)Substitute.For(interfaces.ToArray(), []);
        game.DisplayName.Returns("Test Game");
        if (mountable) ((IMountableGame)game).MountRoots(default!).ReturnsForAnyArgs([Data]);

        var locations = GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, GameFolder));
        return new GameInstallation(new GameLocatorResult { Game = game, Path = GameFolder, Store = GameStore.Steam, StoreIdentifier = "1", LocatorIds = [], Locator = null! }, locations);
    }

    private static DeploymentPlan PlanWith(params DeploymentPlanRoot[] roots) => new([.. roots]);

    private static DeploymentPreflight Evaluate(GameInstallation installation, GamePath? root = null, DeploymentPlan? plan = null, FuseAvailabilityReport? fuse = null, Func<GamePath, bool>? mounted = null, params GamePath[] intrinsics) =>
        FusePreflight.Evaluate(installation, root ?? Data, plan ?? DeploymentPlan.Empty, fuse ?? Usable, mounted ?? (_ => false), intrinsics);

    [Fact]
    public void AcceptsADeclaredRootWhenEverythingHolds() => Evaluate(Installation()).Accepted.Should().BeTrue();

    [Fact]
    public void RefusesAGameThatDeclaresNothing()
    {
        var result = Evaluate(Installation(mountable: false));

        result.Accepted.Should().BeFalse();
        result.Reason.Should().Be("Test Game does not declare Game/Data as a folder that can be mounted; mountable: none");
    }

    [Fact]
    public void RefusesAFolderTheGameDoesNotDeclare()
    {
        var result = Evaluate(Installation(), new GamePath(LocationId.Saves, RelativePath.Empty));

        result.Reason.Should().Be("Test Game does not declare Saves as a folder that can be mounted; mountable: Game/Data");
    }

    [Fact]
    public void RefusesASecondMountedFolder()
    {
        var plan = PlanWith(new DeploymentPlanRoot(new GamePath(LocationId.Game, "Other"), DeploymentBackendId.Fuse, Chosen: true));

        Evaluate(Installation(), plan: plan).Reason.Should().Be("Test Game already mounts Game/Other; one mounted folder per game");
    }

    [Fact]
    public void AcceptsTheRootThatIsAlreadyOnFuse()
    {
        var plan = PlanWith(new DeploymentPlanRoot(Data, DeploymentBackendId.Fuse, Chosen: true));

        Evaluate(Installation(), plan: plan).Accepted.Should().BeTrue();
    }

    [Fact]
    public void RefusesWithTheAvailabilityReason() =>
        Evaluate(Installation(), fuse: new FuseAvailabilityReport(false, "/dev/fuse does not exist", [])).Reason.Should().Be("/dev/fuse does not exist");

    [Fact]
    public void RefusesAFolderThatIsNotThere()
    {
        var installation = Installation();
        Directory.Delete(Path.Combine(_root, "Data"));

        var result = Evaluate(installation, mounted: _ => throw new InvalidOperationException("the folder's parent chain is not resolved before the folder is known to exist"));

        result.Reason.Should().Be($"{GameFolder / "Data"} does not exist");
    }

    [Fact]
    public void AcceptsAFolderSpelledInAnotherCaseOnDisk()
    {
        var installation = Installation(folderName: "data");

        Evaluate(installation).Accepted.Should().BeTrue();
    }

    [Fact]
    public void RefusesAGameThatOrdersPluginsByTime() =>
        Evaluate(Installation(timestamps: true)).Reason.Should().Be("Test Game orders plugins by file time, which a mounted folder cannot present yet");

    [Fact]
    public void RefusesAnIntrinsicFileUnderTheRoot() =>
        Evaluate(Installation(), intrinsics: new GamePath(LocationId.Game, "Data/plugins.txt")).Reason
            .Should().Be("Test Game writes Game/Data/plugins.txt under Game/Data, which a mounted folder cannot serve");

    [Fact]
    public void AcceptsAnIntrinsicFileElsewhere() =>
        Evaluate(Installation(), intrinsics: new GamePath(LocationId.AppData, "plugins.txt")).Accepted.Should().BeTrue();

    [Fact]
    public void RefusesAFolderThatIsAlreadyMounted() =>
        Evaluate(Installation(), mounted: root => root == Data).Reason.Should().Be("Game/Data is already mounted; run silvermodmanager doctor");

    [Fact]
    public void ReportsTheFirstFailingLine()
    {
        var result = Evaluate(Installation(timestamps: true), fuse: new FuseAvailabilityReport(false, "no fuse", []), mounted: _ => true);

        result.Reason.Should().Be("no fuse");
    }
}
