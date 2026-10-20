using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NexusMods.Platform.Proton;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NSubstitute;

// Out-of-process helper for the installation lock tests. flock, O_CLOEXEC and fork/exec behaviour
// can't be observed from inside the xunit process, so each scenario runs here.
//
//   hold-lock <lockPath> <seconds>   take the lock, print "locked", hold, exit
//   probe <lockPath>                 print "held" or "free"
//   write-wrapper <lockPath> <pid>   record wrapper=<pid> in the state file without taking the lock
//   spawn-child-and-exit <lockPath>  take the lock, start a long-lived child, print its pid, exit
//   stop-handler                     install StopHandler, print "ready", then "signal <name> <phase> cancelled=<bool>"
//                                    after the first signal and "count <n>" once a second has been absorbed
//   session <runtimeDir> <preparing|running|finishing> [killfail]
//                                    host a LaunchSession over fakes with a StopHandler; prints "ready", then
//                                    "in <phase>" once the fakes are blocked in that phase, then after the run
//                                    "result <exit code> <events>" where events lists the fakes' calls in order
//   host-lifetime <console|nosignal> run a generic host beside a StopHandler, print "ready", then after a signal
//                                    "stopping" if ApplicationStopping fired or "running" if it did not
return args switch
{
    ["hold-lock", var path, var seconds] => HoldLock(path, double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)),
    ["probe", var path] => Probe(path),
    ["write-wrapper", var path, var pid] => WriteWrapper(path, pid),
    ["spawn-child-and-exit", var path] => SpawnChildAndExit(path),
    ["stop-handler"] => StopHandlerScenario(),
    ["session", var dir, var phase] => SessionScenario(dir, phase, killFail: false),
    ["session", var dir, var phase, "killfail"] => SessionScenario(dir, phase, killFail: true),
    ["host-lifetime", var kind] => HostLifetimeScenario(kind),
    _ => Usage(),
};

static int HoldLock(string path, double seconds)
{
    using var installationLock = new InstallationLock(path);
    if (!installationLock.TryAcquire()) return 3;
    installationLock.WriteState(Environment.ProcessId, "test-loadout", LaunchPhase.Running);
    Console.WriteLine("locked");
    Console.Out.Flush();
    Thread.Sleep(TimeSpan.FromSeconds(seconds));
    return 0;
}

static int Probe(string path)
{
    using var installationLock = new InstallationLock(path);
    Console.WriteLine(installationLock.Probe() ? "held" : "free");
    return 0;
}

static int WriteWrapper(string path, string pid)
{
    using var installationLock = new InstallationLock(path);
    installationLock.WriteWrapperKey("wrapper", pid);
    return 0;
}

static int SpawnChildAndExit(string path)
{
    using var installationLock = new InstallationLock(path);
    if (!installationLock.TryAcquire()) return 3;

    // Standard streams are redirected so the child doesn't keep the caller's pipes open after we exit.
    var child = Process.Start(new ProcessStartInfo("sleep", "60")
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    }) ?? throw new InvalidOperationException("Failed to start sleep");
    Console.WriteLine(child.Id);
    return 0;
}

static int StopHandlerScenario()
{
    using var handler = new StopHandler(NullLogger.Instance);
    var first = new ManualResetEventSlim();
    handler.Stopping += (_, _) => first.Set();
    Console.WriteLine("ready");
    Console.Out.Flush();

    if (!first.Wait(TimeSpan.FromSeconds(30))) return 4;
    Console.WriteLine($"signal {handler.Signal} {handler.PhaseAtSignal} cancelled={handler.PreparingToken.IsCancellationRequested}");
    Console.Out.Flush();

    // The second signal is dropped, so the process must still be here to report it.
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (handler.SignalCount < 2 && DateTime.UtcNow < deadline) Thread.Sleep(10);
    Console.WriteLine($"count {handler.SignalCount} signal {handler.Signal}");
    return handler.SignalCount < 2 ? 5 : 0;
}

static int HostLifetimeScenario(string kind)
{
    var builder = new HostBuilder();
    switch (kind)
    {
        case "console": builder.UseConsoleLifetime(); break;
        case "nosignal": builder.ConfigureServices(services => services.AddSingleton<IHostLifetime, NoSignalHostLifetime>()); break;
        default: return Usage();
    }

    // The StopHandler keeps the process alive after SIGTERM in both modes, so the host's reaction is
    // reported instead of being hidden by the runtime's default terminate.
    using var stop = new StopHandler(NullLogger.Instance);
    var signalled = new ManualResetEventSlim();
    stop.Stopping += (_, _) => signalled.Set();

    using var host = builder.Build();
    var stopping = new ManualResetEventSlim();
    host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(stopping.Set);
    host.Start();
    Console.WriteLine("ready");
    Console.Out.Flush();

    if (!signalled.Wait(TimeSpan.FromSeconds(30))) return 4;
    // ConsoleLifetime reacts in the same signal dispatch; the window only has to outlast that.
    Console.WriteLine(stopping.Wait(TimeSpan.FromSeconds(2)) ? "stopping" : "running");
    Console.Out.Flush();
    host.StopAsync().GetAwaiter().GetResult();
    return 0;
}

static int SessionScenario(string runtimeDir, string blockIn, bool killFail)
{
    var events = new ConcurrentQueue<string>();
    using var stop = new StopHandler(NullLogger.Instance);
    var signalled = new ManualResetEventSlim();

    // Each fake parks in the phase under test until the first signal has been delivered, so the signal is
    // guaranteed to land in that phase. Stopping runs its handlers in subscription order, so this one is
    // added only once the session has subscribed: the session has recorded the stop before a fake moves on.
    void Park(string phase)
    {
        if (blockIn != phase) return;
        stop.Stopping += (_, _) => signalled.Set();
        Console.WriteLine($"in {phase}");
        Console.Out.Flush();
        signalled.Wait(TimeSpan.FromSeconds(30));
    }

    var synchronizer = new ScenarioSynchronizer(events, Park);
    var processes = new ScenarioProcessFactory(events, signalled, blockIn == "running", killFail, Park);
    var lockFactory = Substitute.For<IInstallationLockFactory>();
    lockFactory.Create(Arg.Any<string>(), Arg.Any<string>())
        .Returns(call => new InstallationLock(InstallationLock.PathFor(runtimeDir, call.ArgAt<string>(0), call.ArgAt<string>(1))));
    var gate = new ScenarioGate(events);

    var game = Substitute.For<IGameData>();
    game.DisplayName.Returns("Test Game");
    var folder = FileSystem.Shared.FromUnsanitizedFullPath(runtimeDir);
    var installation = new GameInstallation(new GameLocatorResult
    {
        Game = game,
        Path = folder,
        Store = GameStore.Steam,
        StoreIdentifier = "42",
        LocatorIds = ImmutableArray<LocatorId>.Empty,
        Locator = Substitute.For<IGameLocator>(),
        Platform = OSPlatform.Windows,
    }, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));

    var session = new LaunchSession(lockFactory, new ActiveLaunchSessions(), synchronizer, processes, [gate],
        Substitute.For<IDesktopNotifier>(), NullLogger<LaunchSession>.Instance, TextWriter.Null, stop);
    Console.WriteLine("ready");
    Console.Out.Flush();

    var result = session.RunAsync(new LaunchContext(installation, LoadoutId.From(1), "test-loadout"), () => new GameProcessSpec(["game.exe"])).GetAwaiter().GetResult();
    session.Release();
    Console.WriteLine($"result {result.ExitCode} {string.Join(',', events)}");
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("usage: silvermod-testhost hold-lock <path> <seconds> | probe <path> | write-wrapper <path> <pid> | spawn-child-and-exit <path> | stop-handler | session <runtimeDir> <preparing|running|finishing> [killfail] | host-lifetime <console|nosignal>");
    return 2;
}

internal sealed class ScenarioSynchronizer(ConcurrentQueue<string> events, Action<string> park) : ILaunchSynchronizer
{
    private int _calls;

    public Task Synchronize(LoadoutId loadoutId)
    {
        var call = Interlocked.Increment(ref _calls);
        events.Enqueue("sync");
        park(call == 1 ? "preparing" : "finishing");
        return Task.CompletedTask;
    }
}

internal sealed class ScenarioGate(ConcurrentQueue<string> events) : ILaunchGate
{
    public Veto? Check(LaunchContext context) => null;

    public Task Prepare(LaunchContext context, CancellationToken cancellationToken)
    {
        events.Enqueue("prepare");
        return Task.CompletedTask;
    }

    public Task Finish(LaunchContext context)
    {
        events.Enqueue("finish");
        return Task.CompletedTask;
    }
}

internal sealed class ScenarioProcessFactory(ConcurrentQueue<string> events, ManualResetEventSlim signalled, bool blockUntilTerminated, bool killFail, Action<string> park) : IGameProcessFactory
{
    public IGameProcess Start(GameProcessSpec spec, GameInstallation installation)
    {
        events.Enqueue("start");
        return new ScenarioProcess(events, signalled, blockUntilTerminated, killFail, park);
    }
}

internal sealed class ScenarioProcess(ConcurrentQueue<string> events, ManualResetEventSlim signalled, bool blockUntilTerminated, bool killFail, Action<string> park) : IGameProcess
{
    private readonly ManualResetEventSlim _terminated = new();

    public int Pid => 4321;

    public Task<GameProcessResult> WaitAsync(CancellationToken cancellationToken = default)
    {
        park("running");
        if (blockUntilTerminated) _terminated.Wait(TimeSpan.FromSeconds(30));
        events.Enqueue("exit");
        return Task.FromResult(new GameProcessResult(0, killFail ? new TrackedProcess(4242, "wineserver") : null));
    }

    public void Terminate()
    {
        events.Enqueue("terminate");
        // Hold the terminate open so a second signal can arrive while it is in progress.
        signalled.Wait(TimeSpan.FromSeconds(30));
        Thread.Sleep(300);
        _terminated.Set();
    }

    public void Dispose() => _terminated.Dispose();
}
