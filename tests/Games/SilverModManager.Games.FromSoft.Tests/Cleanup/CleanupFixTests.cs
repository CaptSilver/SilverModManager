using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.FromSoft.Cleanup;
using SilverModManager.Games.FromSoft.Tests.Fixtures;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk;

namespace SilverModManager.Games.FromSoft.Tests.Cleanup;

/// <summary>
/// The Fix button's service: the privileged step through a fake <c>pkexec</c>, then the plan again, then the apply
/// only when that plan is the confirmed one. The immutable attribute is a flag on a fake probe and no process starts.
/// </summary>
public class CleanupFixTests(ITestOutputHelper helper) : AEldenRingTest<CleanupFixTests>(helper, dirtyGameFolder: true)
{
    private sealed class FakeImmutableProbe : IImmutableFlagProbe
    {
        public bool Immutable { get; set; }
        public bool IsImmutable(AbsolutePath file) => Immutable;
    }

    private FakeImmutableProbe Probe { get; } = new();
    private FakeProcessRunner Process { get; } = new();
    private IGameLaunchSession LaunchSession { get; } = Substitute.For<IGameLaunchSession>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        services.Replace(ServiceDescriptor.Singleton<IImmutableFlagProbe>(Probe));
        services.Replace(ServiceDescriptor.Singleton(Substitute.For<ISteamLaunchOptionWriter>()));
        services.Replace(ServiceDescriptor.Singleton(LaunchSession));
        services.Replace(ServiceDescriptor.Singleton<IProcessRunner>(Process));
        return services;
    }

    private CleanupService Service => ServiceProvider.GetRequiredService<CleanupService>();
    private AbsolutePath Game => RealGameFolder;

    private static ManualCommandPrompt NoPrompt => (_, _, _, _) => throw new InvalidOperationException("The user must not be asked.");

    /// <summary>Simulates a successful pkexec: the flag is gone when the command returns.</summary>
    private void PkexecClearsTheFlag() => Process.OnRun = _ => Probe.Immutable = false;

    [Fact]
    public async Task ThePasswordPromptClearsTheFlagThenTheConfirmedStepsRun()
    {
        var loadout = await CreateLoadout();
        Probe.Immutable = true;
        PkexecClearsTheFlag();
        var confirmed = Service.Plan(loadout);

        var outcome = await Service.FixAsync(loadout, confirmed, NoPrompt);

        outcome.Status.Should().Be(CleanupFixStatus.Applied);
        outcome.Result!.Failure.Should().BeNull();
        outcome.Result.Done.Should().Equal(confirmed.Steps.Where(s => s.Acts).Select(s => s with { BlockedReason = null }));
        var command = Process.Commands.Should().ContainSingle().Subject;
        command.TargetFilePath.Should().Be("pkexec");
        command.Arguments.Should().Be($"/usr/bin/chattr -i \"{Game / "start_protected_game.exe"}\"");
        (await (Game / "start_protected_game.exe").ReadAllTextAsync()).Should().Be("anti-cheat launcher");
        (Game / "start_protected_game.exe.erm-backup").FileExists.Should().BeFalse();
        (Game / "mods").DirectoryExists().Should().BeFalse();
    }

    [Fact]
    public async Task WithoutAPrivilegedStepNothingElevatedRuns()
    {
        var loadout = await CreateLoadout();
        var confirmed = Service.Plan(loadout);

        var outcome = await Service.FixAsync(loadout, confirmed, NoPrompt);

        outcome.Status.Should().Be(CleanupFixStatus.Applied);
        outcome.Result!.Succeeded.Should().BeTrue();
        Process.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task DismissingThePasswordDialogChangesNothingAndDoesNotAskTheUserToRunACommand()
    {
        var loadout = await CreateLoadout();
        Probe.Immutable = true;
        Process.ExitCode = 126;
        var confirmed = Service.Plan(loadout);
        var before = SnapshotOfGameFolder();

        var outcome = await Service.FixAsync(loadout, confirmed, NoPrompt);

        outcome.Status.Should().Be(CleanupFixStatus.Cancelled);
        outcome.Result.Should().BeNull();
        SnapshotOfGameFolder().Should().Equal(before);
    }

    [Theory]
    [InlineData(127)]
    [InlineData(1)]
    public async Task WhenPkexecCannotRunTheUserIsShownTheExactCommandAndTheFixContinuesOnceTheFlagIsGone(int exitCode)
    {
        var loadout = await CreateLoadout();
        Probe.Immutable = true;
        Process.ExitCode = exitCode;
        var confirmed = Service.Plan(loadout);
        string? shown = null;
        bool? clearedWhileShown = null;

        var outcome = await Service.FixAsync(loadout, confirmed, (command, _, isCleared, _) =>
        {
            shown = command;
            clearedWhileShown = isCleared();
            Probe.Immutable = false;
            return Task.FromResult(true);
        });

        shown.Should().Be($"sudo chattr -i '{Game / "start_protected_game.exe"}'");
        clearedWhileShown.Should().BeFalse();
        outcome.Status.Should().Be(CleanupFixStatus.Applied);
        outcome.Result!.Succeeded.Should().BeTrue();
        (await (Game / "start_protected_game.exe").ReadAllTextAsync()).Should().Be("anti-cheat launcher");
    }

    [Fact]
    public async Task AMissingPkexecIsTheSameFallback()
    {
        var loadout = await CreateLoadout();
        Probe.Immutable = true;
        Process.Throws = new System.ComponentModel.Win32Exception(2);
        var confirmed = Service.Plan(loadout);
        string? why = null;

        var outcome = await Service.FixAsync(loadout, confirmed, (_, reason, _, _) =>
        {
            why = reason;
            Probe.Immutable = false;
            return Task.FromResult(true);
        });

        why.Should().Contain("not installed");
        outcome.Status.Should().Be(CleanupFixStatus.Applied);
    }

    [Fact]
    public async Task APkexecThatSucceedsButLeavesTheFlagIsNotTrusted()
    {
        var loadout = await CreateLoadout();
        Probe.Immutable = true;
        var confirmed = Service.Plan(loadout);
        var before = SnapshotOfGameFolder();
        string? why = null;

        var outcome = await Service.FixAsync(loadout, confirmed, (_, reason, _, _) =>
        {
            why = reason;
            return Task.FromResult(false);
        });

        why.Should().Contain("still immutable");
        outcome.Status.Should().Be(CleanupFixStatus.Cancelled);
        SnapshotOfGameFolder().Should().Equal(before);
    }

    [Fact]
    public async Task GivingUpOnTheManualCommandChangesNothing()
    {
        var loadout = await CreateLoadout();
        Probe.Immutable = true;
        Process.ExitCode = 127;
        var confirmed = Service.Plan(loadout);
        var before = SnapshotOfGameFolder();

        var outcome = await Service.FixAsync(loadout, confirmed, (_, _, _, _) => Task.FromResult(false));

        outcome.Status.Should().Be(CleanupFixStatus.Cancelled);
        outcome.Message.Should().Contain("start_protected_game.exe").And.Contain("immutable");
        SnapshotOfGameFolder().Should().Equal(before);
    }

    [Fact]
    public async Task SayingDoneWhileTheFlagIsStillSetDoesNotContinue()
    {
        var loadout = await CreateLoadout();
        Probe.Immutable = true;
        Process.ExitCode = 127;
        var confirmed = Service.Plan(loadout);
        var before = SnapshotOfGameFolder();

        var outcome = await Service.FixAsync(loadout, confirmed, (_, _, _, _) => Task.FromResult(true));

        outcome.Status.Should().Be(CleanupFixStatus.Cancelled);
        SnapshotOfGameFolder().Should().Equal(before);
    }

    [Fact]
    public async Task AFolderThatChangedWhileThePasswordWasAskedGivesTheNewPlanBackAndAppliesNothing()
    {
        var loadout = await CreateLoadout();
        Probe.Immutable = true;
        var confirmed = Service.Plan(loadout);
        Process.OnRun = _ =>
        {
            Probe.Immutable = false;
            File.WriteAllText((Game / "ReShade.log").ToString(), "log");
        };

        var outcome = await Service.FixAsync(loadout, confirmed, NoPrompt);

        outcome.Status.Should().Be(CleanupFixStatus.PlanChanged);
        outcome.Result.Should().BeNull();
        outcome.NewPlan!.Steps.Should().Contain(s => s.Kind == CleanupStepKind.Delete && s.Source == "ReShade.log");
        (Game / "start_protected_game.exe.erm-backup").FileExists.Should().BeTrue("the confirmed steps were not applied");
        (Game / "mods").DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public async Task ClearingTheFlagUnblocksOnlyTheStepThatWaitedForIt()
    {
        var loadout = await CreateLoadout();
        Probe.Immutable = true;
        PkexecClearsTheFlag();
        var plan = Service.Plan(loadout);
        var delete = plan.Steps.First(s => s.Kind == CleanupStepKind.Delete);
        // The user confirmed this delete as blocked for another reason, so it must not run just because the
        // attribute is gone.
        var confirmed = new CleanupPlan([..plan.Steps.Select(s => s == delete ? s with { BlockedReason = "something else" } : s)]);

        var outcome = await Service.FixAsync(loadout, confirmed, NoPrompt);

        outcome.Status.Should().Be(CleanupFixStatus.PlanChanged);
        (Game / "start_protected_game.exe.erm-backup").FileExists.Should().BeTrue("nothing was applied");
        (Game / "mods").DirectoryExists().Should().BeTrue();
    }

    [Fact]
    public async Task ThePlanReturnedAfterAChangeAppliesWhenConfirmedAgain()
    {
        var loadout = await CreateLoadout();
        Probe.Immutable = true;
        var confirmed = Service.Plan(loadout);
        Process.OnRun = _ =>
        {
            Probe.Immutable = false;
            File.WriteAllText((Game / "ReShade.log").ToString(), "log");
        };
        var changed = await Service.FixAsync(loadout, confirmed, NoPrompt);

        var outcome = await Service.FixAsync(loadout, changed.NewPlan!, NoPrompt);

        outcome.Status.Should().Be(CleanupFixStatus.Applied);
        outcome.Result!.Succeeded.Should().BeTrue();
        (Game / "ReShade.log").FileExists.Should().BeFalse();
        Process.Commands.Should().ContainSingle("the second run had no privileged step");
    }

    [Fact]
    public async Task ARunningGameStopsTheFixBeforeAnyPasswordIsAsked()
    {
        var loadout = await CreateLoadout();
        Probe.Immutable = true;
        var confirmed = Service.Plan(loadout);
        LaunchSession.IsRunning(Arg.Any<SilverModManager.Sdk.Games.GameInstallation>()).Returns(true);

        var outcome = await Service.FixAsync(loadout, confirmed, NoPrompt);

        outcome.Status.Should().Be(CleanupFixStatus.Cancelled);
        outcome.Message.Should().Contain("running");
        Process.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyingARawPlanWithThePrivilegedStepStillRefuses()
    {
        var loadout = await CreateLoadout();
        Probe.Immutable = true;
        var before = SnapshotOfGameFolder();

        var result = await Service.ApplyAsync(loadout, Service.Plan(loadout));

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Contain("immutable");
        Process.Commands.Should().BeEmpty();
        SnapshotOfGameFolder().Should().Equal(before);
    }
}
