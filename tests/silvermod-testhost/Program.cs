using System.Diagnostics;
using NexusMods.Platform.Proton.Launch;

// Out-of-process helper for the installation lock tests. flock, O_CLOEXEC and fork/exec behaviour
// can't be observed from inside the xunit process, so each scenario runs here.
//
//   hold-lock <lockPath> <seconds>   take the lock, print "locked", hold, exit
//   probe <lockPath>                 print "held" or "free"
//   write-wrapper <lockPath> <pid>   record wrapper=<pid> in the state file without taking the lock
//   spawn-child-and-exit <lockPath>  take the lock, start a long-lived child, print its pid, exit
return args switch
{
    ["hold-lock", var path, var seconds] => HoldLock(path, double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)),
    ["probe", var path] => Probe(path),
    ["write-wrapper", var path, var pid] => WriteWrapper(path, pid),
    ["spawn-child-and-exit", var path] => SpawnChildAndExit(path),
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

static int Usage()
{
    Console.Error.WriteLine("usage: silvermod-testhost hold-lock <path> <seconds> | probe <path> | write-wrapper <path> <pid> | spawn-child-and-exit <path>");
    return 2;
}
