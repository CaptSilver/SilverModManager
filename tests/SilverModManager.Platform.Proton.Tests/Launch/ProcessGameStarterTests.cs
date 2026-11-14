using FluentAssertions;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Starters;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Launch.Starters;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Tests.Launch;

public class ProcessGameStarterTests(ITestOutputHelper helper) : AStubbedGameIsolatedTest<ProcessGameStarterTests>(helper)
{
    private readonly IGameProcessFactory _processes = Substitute.For<IGameProcessFactory>();
    private readonly IGameProcess _process = Substitute.For<IGameProcess>();

    private static readonly StartTiming Timing = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeProvider.System);

    private DeploymentLaunchRoots RootsOf(params string[] folders)
    {
        var plans = Substitute.For<IDeploymentPlanStore>();
        plans.Load(Arg.Any<IDb>(), Arg.Any<GameInstallMetadataId>()).Returns(new DeploymentPlan(
            [new DeploymentPlanRoot(new GamePath(LocationId.Game, RelativePath.Empty), DeploymentBackendId.Symlink)]));
        var backend = Substitute.For<IDeploymentBackend>();
        backend.Id.Returns(DeploymentBackendId.Symlink);
        backend.RequiredFilesystems(Arg.Any<GameInstallation>(), Arg.Any<GamePath>())
            .Returns(_ => folders.Select(f => FileSystem.FromUnsanitizedFullPath(f)).ToArray());
        return new DeploymentLaunchRoots(plans, [backend], Connection);
    }

    private async Task<(ProcessGameStarter Starter, GameStartRequest Request)> Rig(string[]? callerArgs = null, string[]? backendRoots = null)
    {
        _processes.Start(Arg.Any<GameProcessSpec>(), Arg.Any<GameInstallation>(), Arg.Any<IReadOnlySet<int>?>()).Returns(_process);
        _process.WaitAsync(Arg.Any<CancellationToken>(), Arg.Any<IProgress<RunningProcesses>?>()).Returns(new GameProcessResult(0, null));
        var loadout = await CreateLoadout();
        var spec = new LaunchSpec(FileSystem.FromUnsanitizedFullPath("/usr/bin/bwrap"), ["--unshare-user", "--", "loader"])
        {
            ReplacesCommand = true,
            FilesystemRoots = ["/spec/root"],
        };
        var request = new GameStartRequest(loadout, loadout.InstallationInstance, spec, callerArgs ?? [], IsPrimaryExe: false, UseShell: false, Timing);
        return (new ProcessGameStarter(_processes, RootsOf(backendRoots ?? ["/backend/root"])), request);
    }

    [Fact]
    public async Task Start_HandsTheComposedCommandToTheProcessFactoryWithTheSpecsRoots()
    {
        var (starter, request) = await Rig();

        var outcome = await starter.Start(request, Substitute.For<IGameLaunch>(), CancellationToken.None);

        outcome.Should().Be(GameStartOutcome.Exited);
        var started = _processes.ReceivedCalls().Single().GetArguments()[0].Should().BeOfType<GameProcessSpec>().Subject;
        started.Argv.Should().Equal("/usr/bin/bwrap", "--unshare-user", "--", "loader");
        started.FilesystemRoots.Should().BeEquivalentTo(["/backend/root", "/spec/root"]);
        await _process.Received(1).WaitAsync(Arg.Any<CancellationToken>(), Arg.Any<IProgress<RunningProcesses>?>());
        _process.Received(1).Dispose();
    }

    [Fact]
    public async Task Start_TerminatesTheChildWhenTheWaitThrows()
    {
        var (starter, request) = await Rig();
        _process.WaitAsync(Arg.Any<CancellationToken>(), Arg.Any<IProgress<RunningProcesses>?>()).Returns<Task<GameProcessResult>>(_ => throw new OperationCanceledException());

        var act = async () => await starter.Start(request, Substitute.For<IGameLaunch>(), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _process.Received(1).Terminate();
    }

    [Fact]
    public async Task CheckCanStart_RefusesCallerArguments()
    {
        var (starter, request) = await Rig(callerArgs: ["-windowed"]);

        var act = () => starter.CheckCanStart(request);

        act.Should().Throw<GameLaunchException>().Which.Message.Should().Contain("-windowed");
    }

    [Fact]
    public async Task CheckCanStart_AllowsTheSpecsOwnArguments()
    {
        var (starter, request) = await Rig();

        var act = () => starter.CheckCanStart(request);

        act.Should().NotThrow("Spec.ExtraArgs is the sandbox's argv, not something the user passed");
    }
}
