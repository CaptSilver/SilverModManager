using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Platform.Proton.Launch;

// Out-of-process helper for the installation lock tests. flock, O_CLOEXEC and fork/exec behaviour
// can't be observed from inside the xunit process, so each scenario runs here.
//
//   hold-lock <lockPath> <seconds>   take the lock, print "locked", hold, exit
//   probe <lockPath>                 print "held" or "free"
//   write-wrapper <lockPath> <pid>   record wrapper=<pid> in the state file without taking the lock
//   spawn-child-and-exit <lockPath>  take the lock, start a long-lived child, print its pid, exit
//   stop-handler                     install StopHandler, print "ready", then "signal <name> <phase> cancelled=<bool>"
//                                    after the first signal and "count <n>" once a second has been absorbed
//   host-lifetime <console|nosignal> run a generic host beside a StopHandler, print "ready", then after a signal
//                                    "stopping" if ApplicationStopping fired or "running" if it did not
return args switch
{
    ["hold-lock", var path, var seconds] => HoldLock(path, double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)),
    ["probe", var path] => Probe(path),
    ["write-wrapper", var path, var pid] => WriteWrapper(path, pid),
    ["spawn-child-and-exit", var path] => SpawnChildAndExit(path),
    ["stop-handler"] => StopHandlerScenario(),
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

static int Usage()
{
    Console.Error.WriteLine("usage: silvermod-testhost hold-lock <path> <seconds> | probe <path> | write-wrapper <path> <pid> | spawn-child-and-exit <path> | stop-handler | host-lifetime <console|nosignal>");
    return 2;
}
