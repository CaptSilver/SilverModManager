using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.SingleProcess;

namespace SilverModManager.CLI.Tests;

public class ForwardedLaunchHandlerTests
{
    private static IJob JobOf(IJobDefinition definition, JobStatus status)
    {
        var job = Substitute.For<IJob>();
        job.Definition.Returns(definition);
        job.Status.Returns(status);
        return job;
    }

    [Theory]
    [InlineData(JobStatus.Created)]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Paused)]
    public void AnUnfinishedApplyForALoadoutOfTheInstallationIsInProgress(JobStatus status)
    {
        var job = JobOf(new SynchronizeLoadoutJob(LoadoutId.From(5)), status);

        ForwardedLaunchHandler.ApplyInProgress([job], id => id == LoadoutId.From(5)).Should().BeTrue();
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Failed)]
    public void AFinishedApplyIsNotInProgress(JobStatus status)
    {
        var job = JobOf(new SynchronizeLoadoutJob(LoadoutId.From(5)), status);

        ForwardedLaunchHandler.ApplyInProgress([job], _ => true).Should().BeFalse();
    }

    [Fact]
    public void AnApplyForAnotherInstallationDoesNotCount()
    {
        var job = JobOf(new SynchronizeLoadoutJob(LoadoutId.From(5)), JobStatus.Running);

        ForwardedLaunchHandler.ApplyInProgress([job], _ => false).Should().BeFalse();
    }

    [Fact]
    public void ADifferentKindOfJobDoesNotCount()
    {
        var job = JobOf(Substitute.For<IJobDefinition>(), JobStatus.Running);

        ForwardedLaunchHandler.ApplyInProgress([job], _ => true).Should().BeFalse();
    }

    [Fact]
    public void TheForwardedLaunchCarriesTheCommandAndTheEnvironmentAndFolderItWasStartedIn()
    {
        LaunchArgs.TryParse(["My Loadout", "--game", "1091500", "--", "proton", "waitforexitandrun", "game.exe"], out var args, out _).Should().BeTrue();
        var environment = new Dictionary<string, string> { ["SteamAppId"] = "1091500" };

        var launch = LaunchRun.ToForwardedLaunch(args!, environment, "/games/peak");

        launch.LoadoutName.Should().Be("My Loadout");
        launch.Game.Should().Be("1091500");
        launch.Command.Should().Equal("proton", "waitforexitandrun", "game.exe");
        launch.Environment.Should().BeSameAs(environment);
        launch.WorkingDirectory.Should().Be("/games/peak");
    }

    [Fact]
    public void TheForwardedLaunchNamesTheForwardingProcess()
    {
        LaunchArgs.TryParse(["My Loadout", "--", "game.exe"], out var args, out _).Should().BeTrue();

        LaunchRun.ToForwardedLaunch(args!, new Dictionary<string, string>(), null).ClientPid.Should().Be(Environment.ProcessId);
    }

    [Fact]
    public void TheGameOfAForwardedLaunchIsSweptWithoutTheForwardingProcess()
    {
        var launch = new ForwardedLaunch("L", null, ["game.exe"], new Dictionary<string, string>(), null, ClientPid: 4242);
        var plan = new LaunchPlan(new GameProcessSpec(["game.exe"]));

        LaunchRun.InForwardedEnvironment(plan, launch).Process.ExcludedPids.Should().BeEquivalentTo(new[] { 4242 });
    }

    [Fact]
    public void ALaunchFromAnOlderClientThatSentNoPidExcludesNobody()
    {
        var launch = new ForwardedLaunch("L", null, ["game.exe"], new Dictionary<string, string>(), null);

        LaunchRun.InForwardedEnvironment(new LaunchPlan(new GameProcessSpec(["game.exe"])), launch).Process.ExcludedPids.Should().BeNull();
    }

    [Theory]
    [InlineData(PosixSignal.SIGTERM)]
    [InlineData(PosixSignal.SIGHUP)]
    [InlineData(PosixSignal.SIGINT)]
    public void AStopSignalIsLoggedByNameAndCancelsTheForwardedLaunch(PosixSignal signal)
    {
        var logger = new RecordingLogger();
        using var stop = new CancellationTokenSource();
        var context = new PosixSignalContext(signal);

        LaunchRun.RelayStop(logger, stop, context);

        logger.Messages.Should().ContainSingle().Which.Should().Contain(signal.ToString());
        context.Cancel.Should().BeTrue("the signal must not kill the process before the window has ended the game");
        stop.IsCancellationRequested.Should().BeTrue();
    }

    private sealed class RecordingLogger : ILogger
    {
        public readonly List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add($"{logLevel}: {formatter(state, exception)}");
    }

    [Fact]
    public async Task ALoadoutThatDoesNotExistIsDeclinedWithTheResolversMessage_AndNothingRuns()
    {
        using var host = LaunchRunTests.BuildRunHost();
        var handler = new ForwardedLaunchHandler(host.Services);

        var admission = await handler.AdmitAsync(new ForwardedLaunch("Missing Loadout", null, ["game.exe"], new Dictionary<string, string>(), null));

        admission.IsAccepted.Should().BeFalse();
        admission.DeclineReason.Should().Contain("Missing Loadout");
        host.Services.GetRequiredService<LaunchSessionSlot>().Current.Should().BeNull();
    }
}
