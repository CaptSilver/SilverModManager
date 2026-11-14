using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class StaleMountSweepTests
{
    internal static GameInstallation Game(string name)
    {
        var data = Substitute.For<IGameData>();
        data.DisplayName.Returns(name);
        var folder = FileSystem.Shared.FromUnsanitizedFullPath($"/tmp/{name}");
        var result = new GameLocatorResult
        {
            Game = data,
            Path = folder,
            Store = GameStore.Steam,
            StoreIdentifier = name,
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));
    }

    [Fact]
    public async Task RunsTheFinisherForEveryManagedInstallation()
    {
        var a = Game("A");
        var b = Game("B");
        var installations = Substitute.For<IManagedInstallations>();
        installations.List().Returns([new ManagedInstallation(a, []), new ManagedInstallation(b, [])]);
        var finisher = Substitute.For<IPendingLaunchFinisher>();
        finisher.FinishIfPending(Arg.Any<GameInstallation>(), Arg.Any<CancellationToken>())
            .Returns(call => new LaunchFinishReport(LaunchFinishOutcome.NothingPending, call.Arg<GameInstallation>()));

        var reports = await new StaleMountSweep(installations, finisher).Run(CancellationToken.None);

        reports.Select(r => r.Installation).Should().Equal(a, b);
        await finisher.Received(1).FinishIfPending(a, Arg.Any<CancellationToken>());
        await finisher.Received(1).FinishIfPending(b, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReturnsTheFinishersReportsAsTheyCame()
    {
        var a = Game("A");
        var installations = Substitute.For<IManagedInstallations>();
        installations.List().Returns([new ManagedInstallation(a, [])]);
        var finisher = Substitute.For<IPendingLaunchFinisher>();
        finisher.FinishIfPending(a, Arg.Any<CancellationToken>()).Returns(new LaunchFinishReport(LaunchFinishOutcome.Finished, a, "released"));

        var reports = await new StaleMountSweep(installations, finisher).Run(CancellationToken.None);

        reports.Should().ContainSingle().Which.Message.Should().Be("released");
    }

    [Fact]
    public async Task AnInstallationTheFilterLeavesOutIsNotAskedAbout()
    {
        var a = Game("A");
        var b = Game("B");
        var installations = Substitute.For<IManagedInstallations>();
        installations.List().Returns([new ManagedInstallation(a, []), new ManagedInstallation(b, [])]);
        var finisher = Substitute.For<IPendingLaunchFinisher>();
        finisher.FinishIfPending(Arg.Any<GameInstallation>(), Arg.Any<CancellationToken>())
            .Returns(call => new LaunchFinishReport(LaunchFinishOutcome.NothingPending, call.Arg<GameInstallation>()));

        var reports = await new StaleMountSweep(installations, finisher).Run(installation => installation != a, CancellationToken.None);

        reports.Select(r => r.Installation).Should().Equal(b);
        await finisher.DidNotReceive().FinishIfPending(a, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoManagedInstallationsMeansNoReports()
    {
        var installations = Substitute.For<IManagedInstallations>();
        installations.List().Returns([]);

        var reports = await new StaleMountSweep(installations, Substitute.For<IPendingLaunchFinisher>()).Run(CancellationToken.None);

        reports.Should().BeEmpty();
    }
}
