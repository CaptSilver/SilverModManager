using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;
using NSubstitute;

namespace NexusMods.Abstractions.Games.Tests;

public class RunGameToolTests(ITestOutputHelper helper) : AIsolatedGameTest<RunGameToolTests, StubbedGame>(helper)
{
    private readonly CancellationTokenSource _cts = new();
    private bool _cancelOnOpen = true;
    private readonly List<Uri> _openedUris = [];
    private readonly List<string[]> _eventsAtOpen = [];
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        // Registered after the base graph so the fake wins: nothing here may reach the real desktop.
        // Opening the URI cancels the run, which stops it before it starts polling the process table for Steam.
        var osInterop = Substitute.For<IOSInterop>();
        osInterop.When(x => x.OpenUri(Arg.Any<Uri>())).Do(call =>
        {
            _openedUris.Add(call.Arg<Uri>());
            lock (Session.Events) _eventsAtOpen.Add(Session.Events.ToArray());
            if (_cancelOnOpen) _cts.Cancel();
        });

        return base.AddServices(services)
            .AddGame<StubbedGame>()
            // Steam locator ids are numeric build ids, and the stubbed file hasher only knows this one for Steam.
            // The store identifier is what ends up in the steam:// URL.
            .AddSingleton<IGameLocator>(s => new UniversalStubbedGameLocator<StubbedGame>(
                s,
                s.GetRequiredService<IFileSystem>(),
                s.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam])
            {
                LocatorIds = [LocatorId.From("3976631895")],
            })
            .AddSingleton(osInterop)
            .AddSingleton(_processRunner)
            .ReplaceDesktopNotifier();
    }

    private FakeGameLaunchSession Session => ServiceProvider.GetRequiredService<FakeGameLaunchSession>();
    private RecordingDesktopNotifier Notifier => (RecordingDesktopNotifier)ServiceProvider.GetRequiredService<IDesktopNotifier>();

    // Short waits so the timeout cases don't take minutes; the production values are the base class defaults.
    private sealed class FastTool(IServiceProvider provider, StubbedGame game) : RunGameTool<StubbedGame>(provider, game)
    {
        protected override TimeSpan StartTimeout => TimeSpan.FromMilliseconds(80);
        protected override TimeSpan PollInterval => TimeSpan.FromMilliseconds(5);
    }

    private uint AppId => uint.Parse(GameInstallation.LocatorResult.StoreIdentifier);

    private FakeProcessTracker Tracker => ServiceProvider.GetRequiredService<FakeProcessTracker>();

    private sealed class ArgsTool(IServiceProvider provider, StubbedGame game, string[] extraArgs) : RunGameTool<StubbedGame>(provider, game)
    {
        public override async ValueTask<LaunchSpec> GetLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        {
            var baseSpec = await base.GetLaunchSpec(loadout, cancellationToken);
            return baseSpec with { ExtraArgs = extraArgs };
        }
    }

    [Fact]
    public async Task BaseSpec_IsThePrimaryFileWithNoArguments()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());
        IRunGameTool tool = new RunGameTool<StubbedGame>(ServiceProvider, Game);

        var spec = await tool.GetLaunchSpec(loadout, CancellationToken.None);

        spec.Exe.Should().Be(GameInstallation.Locations.ToAbsolutePath(Game.GetPrimaryFile(GameInstallation)));
        spec.ExtraArgs.Should().BeEmpty();
    }

    [Fact]
    public async Task ExtraArgs_NeverReachTheSteamUrl()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());
        var tool = new ArgsTool(ServiceProvider, Game, ["-extra"]);

        await tool.Execute(loadout, _cts.Token, ["-caller"]);

        // Steam asks the user to confirm any arguments it is handed in the URL.
        var appId = GameInstallation.LocatorResult.StoreIdentifier;
        _openedUris.Should().ContainSingle()
            .Which.Should().Be(new Uri($"steam://run/{appId}"));
    }

    [Fact]
    public async Task ExtraArgs_AloneDoNotReachTheSteamUrl()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());
        var tool = new ArgsTool(ServiceProvider, Game, ["-extra"]);

        await tool.Execute(loadout, _cts.Token, null);

        var appId = GameInstallation.LocatorResult.StoreIdentifier;
        _openedUris.Should().ContainSingle()
            .Which.Should().Be(new Uri($"steam://run/{appId}"));
    }

    [Fact]
    public async Task ExtraArgs_AreHandedToTheLaunchSessionForTheSteamWrapper()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());
        var tool = new ArgsTool(ServiceProvider, Game, ["-extra"]);

        await tool.Execute(loadout, _cts.Token, null);

        var spec = ServiceProvider.GetRequiredService<FakeGameLaunchSession>().BegunWith.Should().ContainSingle().Which;
        spec.Should().NotBeNull();
        spec!.Exe.Should().Be(GameInstallation.Locations.ToAbsolutePath(Game.GetPrimaryFile(GameInstallation)));
        spec.ExtraArgs.Should().Equal("-extra");
    }

    private sealed class LauncherTool(IServiceProvider provider, StubbedGame game, string[]? extraArgs = null) : RunGameTool<StubbedGame>(provider, game)
    {
        public override async ValueTask<LaunchSpec> GetLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        {
            var baseSpec = await base.GetLaunchSpec(loadout, cancellationToken);
            return baseSpec with { Exe = baseSpec.Exe.Parent.Combine("launcher.exe"), ExtraArgs = extraArgs ?? [] };
        }
    }

    private FakeToolRunner ToolRunner => ServiceProvider.GetRequiredService<FakeToolRunner>();

    // OS interop probes xdg tools through the same runner at startup, so look for the launcher specifically.
    private bool LauncherStartedByProcessRunner()
        => _processRunner.ReceivedCalls().Any(c => c.GetArguments().OfType<CliWrap.Command>().Any(x => x.TargetFilePath.EndsWith("launcher.exe")));

    // The fake prefix resolver hands out a Windows prefix by default; a native build has none.
    private void MakeBuildNative()
        => ServiceProvider.GetRequiredService<FakePrefixResolver>().Resolution = _ => PrefixResolution.Native("runs natively");

    [Fact]
    public async Task Execute_WaitsForGameProcessesTheTrackerFindsAfterTheLaunch()
    {
        // Not the primary file, so the tool starts the launcher itself instead of going through the store.
        MakeBuildNative();
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());
        var tool = new LauncherTool(ServiceProvider, Game);
        var gameExe = GameInstallation.Locations.ToAbsolutePath(Game.GetPrimaryFile(GameInstallation)).FileName;

        // The game is already running once; the launcher starts a second copy that has to exit too.
        var launched = false;
        var polls = 0;
        Tracker.ByName = name =>
        {
            if (name != gameExe) return [];
            if (!launched) return [new TrackedProcess(10, name)];
            polls++;
            return polls == 1 ? [new TrackedProcess(10, name), new TrackedProcess(11, name)] : [new TrackedProcess(10, name)];
        };
        _processRunner.RunAsync(Arg.Any<CliWrap.Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            launched = true;
            return Task.FromResult(new CliWrap.CommandResult(0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        });

        await tool.Execute(loadout, CancellationToken.None, []);

        polls.Should().Be(2, "it polls again while the new process is alive and stops once only the old one is left");
        Tracker.NameQueries.Should().Contain(["launcher.exe", gameExe]);
    }

    [Fact]
    public async Task SteamLaunch_HasTakenTheLockAndMarkedItRunningBeforeTheUriOpens()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());

        await new FastTool(ServiceProvider, Game).Execute(loadout, _cts.Token, []);

        _eventsAtOpen.Should().ContainSingle().Which.Should().Equal("lock", "running");
        Session.Events.Should().Equal("lock", "running", "finish", "release");
    }

    [Fact]
    public async Task SteamLaunch_RefusedBySession_NeverOpensTheUri()
    {
        Session.Refusal = new GameLaunchException("Game is already being launched.");
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());

        var act = async () => await new FastTool(ServiceProvider, Game).Execute(loadout, CancellationToken.None, []);

        await act.Should().ThrowAsync<GameLaunchException>().WithMessage("Game is already being launched.");
        _openedUris.Should().BeEmpty();
        Session.Events.Should().EndWith(["release"]);
    }

    [Fact]
    public async Task SteamLaunch_WithoutSteam_TimesOutFinishesAndNotifies()
    {
        _cancelOnOpen = false;
        Tracker.SteamRunning = false;
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());

        await new FastTool(ServiceProvider, Game).Execute(loadout, CancellationToken.None, []);

        Session.Events.Should().Equal("lock", "running", "finish", "release");
        Notifier.Notifications.Should().ContainSingle()
            .Which.Body.Should().Contain("Steam did not start").And.Contain("5 minutes");
    }

    [Fact]
    public async Task SteamLaunch_WithSteamButNoReaper_TimesOutFinishesAndNotifies()
    {
        _cancelOnOpen = false;
        Tracker.SteamRunning = true;
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());

        await new FastTool(ServiceProvider, Game).Execute(loadout, CancellationToken.None, []);

        Session.Events.Should().Equal("lock", "running", "finish", "release");
        Notifier.Notifications.Should().ContainSingle().Which.Body.Should().Contain("Steam did not start");
    }

    [Fact]
    public async Task SteamLaunch_Cancelled_FinishesAndPostsNothing()
    {
        Tracker.SteamRunning = false;
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());

        await new FastTool(ServiceProvider, Game).Execute(loadout, _cts.Token, []);

        Session.Events.Should().Equal("lock", "running", "finish", "release");
        Notifier.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task SteamLaunch_WaitsForTheReaperOfItsOwnApp_AndEndsWhenItExits()
    {
        _cancelOnOpen = false;
        Tracker.SteamRunning = true;
        var queried = new List<uint>();
        var reaperAlive = 3;
        Tracker.ReaperForApp = id =>
        {
            lock (queried) queried.Add(id);
            // Another game's reaper never answers for this app id.
            return id == AppId && reaperAlive-- > 0 ? new TrackedProcess(77, "reaper") : null;
        };
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());

        await new FastTool(ServiceProvider, Game).Execute(loadout, CancellationToken.None, []);

        queried.Should().OnlyContain(id => id == AppId);
        queried.Count.Should().BeGreaterThan(1, "it keeps asking until the reaper is gone");
        Session.Events.Should().Equal("lock", "running", "finish", "release");
        Notifier.Notifications.Should().BeEmpty();
    }

    [Fact]
    public async Task SteamLaunch_DoesNotTakeOneMissedScanForTheReaperExiting()
    {
        _cancelOnOpen = false;
        Tracker.SteamRunning = true;
        // Found, missed by one scan (a /proc entry that could not be read), then found again before it really exits.
        var answers = new Queue<bool>([true, true, false, true, true]);
        var foundAfterTheMiss = false;
        var missed = false;
        Tracker.ReaperForApp = _ =>
        {
            if (!answers.TryDequeue(out var alive)) return null;
            if (!alive) missed = true;
            else if (missed) foundAfterTheMiss = true;
            return alive ? new TrackedProcess(77, "reaper") : null;
        };
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());

        await new FastTool(ServiceProvider, Game).Execute(loadout, CancellationToken.None, []);

        foundAfterTheMiss.Should().BeTrue("a single scan that misses the reaper must not end the wait, or the game gets shut down while it runs");
        Session.Events.Should().Equal("lock", "running", "finish", "release");
    }

    [Fact]
    public async Task SteamLaunch_KeepsWaitingPastTheTimeoutWhileAWrapperHasRegistered()
    {
        _cancelOnOpen = false;
        Tracker.SteamRunning = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var reaperReturned = 0;
        // The reaper only shows up well after the timeout; the wrapper key is what says the launch is alive.
        Tracker.ReaperForApp = _ => clock.ElapsedMilliseconds > 250 && reaperReturned++ < 1 ? new TrackedProcess(77, "reaper") : null;
        Session.WrapperActive = () => reaperReturned == 0;
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());

        await new FastTool(ServiceProvider, Game).Execute(loadout, CancellationToken.None, []);

        Notifier.Notifications.Should().BeEmpty("a wrapper that registered during the wait means the game is starting");
        reaperReturned.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task NonPrimaryExe_RunsInsideTheSessionAndFinishesAfterTheProcessesExit()
    {
        MakeBuildNative();
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());
        string[]? eventsAtStart = null;
        _processRunner.RunAsync(Arg.Any<CliWrap.Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            lock (Session.Events) eventsAtStart = Session.Events.ToArray();
            return Task.FromResult(new CliWrap.CommandResult(0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        });

        await new LauncherTool(ServiceProvider, Game).Execute(loadout, CancellationToken.None, []);

        eventsAtStart.Should().Equal("lock", "running");
        Session.Events.Should().Equal("lock", "running", "finish", "release");
    }

    [Fact]
    public async Task NonPrimaryExe_OnAWindowsBuild_GoesToTheToolRunnerWithTheCallersArgsThenExtraArgs()
    {
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());
        string[]? eventsAtStart = null;
        ToolRunner.OnRun = () => { lock (Session.Events) eventsAtStart = Session.Events.ToArray(); };

        await new LauncherTool(ServiceProvider, Game, ["-extra"]).Execute(loadout, CancellationToken.None, ["-caller"]);

        var launcher = GameInstallation.Locations.ToAbsolutePath(Game.GetPrimaryFile(GameInstallation)).Parent.Combine("launcher.exe");
        var call = ToolRunner.Calls.Should().ContainSingle().Subject;
        call.Exe.Should().Be(launcher);
        call.Args.Should().Equal("-caller", "-extra");
        LauncherStartedByProcessRunner().Should().BeFalse();
        eventsAtStart.Should().Equal("lock", "running");
        Session.Events.Should().Equal("lock", "running", "finish", "release");
    }

    [Fact]
    public async Task NonPrimaryExe_OnANativeBuild_StillGoesToTheProcessRunner()
    {
        MakeBuildNative();
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());
        CliWrap.Command? started = null;
        _processRunner.RunAsync(Arg.Any<CliWrap.Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            started = call.Arg<CliWrap.Command>();
            return Task.FromResult(new CliWrap.CommandResult(0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        });

        await new LauncherTool(ServiceProvider, Game, ["-extra"]).Execute(loadout, CancellationToken.None, ["-caller"]);

        started.Should().NotBeNull();
        started!.TargetFilePath.Should().EndWith("launcher.exe");
        started.Arguments.Should().Be("-caller -extra");
        ToolRunner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task NonPrimaryExe_WhenNoPrefixIsFoundForANonWindowsBuild_StillGoesToTheProcessRunner()
    {
        // A manually added game: the resolver gives up on it whatever it is, and nothing says it is Windows.
        ServiceProvider.GetRequiredService<FakePrefixResolver>().Resolution = _ => PrefixResolution.Failed("manually added games carry no Wine prefix yet");
        ToolRunner.CanRunTools = false;
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());

        await new LauncherTool(ServiceProvider, Game).Execute(loadout, CancellationToken.None, []);

        LauncherStartedByProcessRunner().Should().BeTrue();
        ToolRunner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task NonPrimaryExe_WhenTheToolRunnerCannotRunIt_ThrowsItsDiagnosticBeforeTheSessionStarts()
    {
        ToolRunner.CanRunTools = false;
        ToolRunner.Diagnostic = "No Wine prefix was found for the stubbed game.";
        var loadout = await LoadoutManager.CreateLoadout(GameInstallation, Guid.NewGuid().ToString());

        var act = async () => await new LauncherTool(ServiceProvider, Game).Execute(loadout, CancellationToken.None, []);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No Wine prefix was found for the stubbed game.*");
        Session.Events.Should().BeEmpty("the loadout must not be applied for a program that cannot start");
        LauncherStartedByProcessRunner().Should().BeFalse();
    }
}
