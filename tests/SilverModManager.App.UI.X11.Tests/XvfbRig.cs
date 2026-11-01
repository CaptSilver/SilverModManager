using System.Collections.Concurrent;
using System.Diagnostics;

namespace SilverModManager.App.UI.X11.Tests;

/// <summary>
/// A private Xvfb display with the test host running on it. The host gets an environment built from nothing: a child
/// started this way is the only setup where libX11's <c>XOpenDisplay(NULL)</c> (which reads the native environment, not
/// .NET's copy) and every managed reader see the same <c>DISPLAY</c>. Nothing here touches the desktop's display or bus.
/// </summary>
internal sealed class XvfbRig : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _xvfb;
    private readonly Process _host;
    private readonly string _scratch;
    private readonly ConcurrentQueue<HostReport> _reports = new();
    private readonly ConcurrentQueue<string> _hostErrors = new();

    private XvfbRig(Process xvfb, Process host, string scratch, string display)
    {
        _xvfb = xvfb;
        _host = host;
        _scratch = scratch;
        DisplayName = display;
    }

    /// <summary>Where the child's files live; the test creates the files it drags here too.</summary>
    public string Scratch => _scratch;

    public string DisplayName { get; }

    /// <summary>The host's first report: its window's XID and the drop border's centre in root coordinates.</summary>
    public HostReport Ready { get; private set; } = new("starting");

    public nint WindowXid => (nint)Ready.Xid;

    public bool HostIsRunning => _host.HasExited is false;

    public string HostStandardError => string.Join(Environment.NewLine, _hostErrors);

    /// <summary>Skips the calling test when Xvfb isn't installed, so the project is green-by-skip where it can't run.</summary>
    public static async Task<XvfbRig> StartAsync()
    {
        var xvfbPath = FindOnPath("Xvfb");
        Assert.SkipUnless(xvfbPath is not null, "Xvfb not installed; dnf install xorg-x11-server-Xvfb in the fedora box");

        var scratch = Directory.CreateTempSubdirectory("xdnd-xvfb-").FullName;
        Process? xvfb = null;
        Process? host = null;
        try
        {
            xvfb = StartXvfb(xvfbPath!, scratch);
            var display = await ReadDisplayAsync(xvfb);
            xvfb.BeginErrorReadLine();
            host = StartHost(display, scratch);

            var rig = new XvfbRig(xvfb, host, scratch, display);
            rig.StartReading();
            var seen = await rig.WaitForAsync("ready", StartupTimeout);
            rig.Ready = seen.First(r => r.Kind == "ready");
            return rig;
        }
        catch
        {
            Kill(host);
            Kill(xvfb);
            Directory.Delete(scratch, recursive: true);
            throw;
        }
    }

    private void StartReading()
    {
        _host.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line && HostReport.TryParse(line) is { } report) _reports.Enqueue(report);
        };
        _host.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line) _hostErrors.Enqueue(line);
        };
        _host.BeginOutputReadLine();
        _host.BeginErrorReadLine();
    }

    /// <summary>Reports the host has written since the last call.</summary>
    public IReadOnlyList<HostReport> TakeReports()
    {
        var taken = new List<HostReport>();
        while (_reports.TryDequeue(out var report)) taken.Add(report);
        return taken;
    }

    /// <summary>Waits until a report of <paramref name="kind"/> has arrived (and returns everything read so far, in order).</summary>
    public async Task<IReadOnlyList<HostReport>> WaitForAsync(string kind, TimeSpan timeout)
    {
        var seen = new List<HostReport>();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            seen.AddRange(TakeReports());
            if (seen.Any(r => r.Kind == kind)) return seen;
            await Task.Delay(10);
        }
        throw new TimeoutException($"The host reported no {kind} within {timeout.TotalSeconds:0.#}s; saw [{string.Join(", ", seen.Select(r => r.Kind))}]. Host stderr:{Environment.NewLine}{HostStandardError}");
    }

    public ValueTask DisposeAsync()
    {
        // Closing stdin is how the host is told to exit; the kill is for a host that is stuck. Both by pid, never by name.
        try { _host.StandardInput.Close(); } catch (IOException) { }
        if (_host.WaitForExit(3000) is false) Kill(_host);
        Kill(_xvfb);
        _host.Dispose();
        _xvfb.Dispose();
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { }
        return ValueTask.CompletedTask;
    }

    private static Process StartXvfb(string path, string scratch)
    {
        // -displayfd 1 writes the chosen display number to stdout. A guessed free number would be wrong: the distrobox
        // shares /tmp/.X11-unix with the host, where the desktop's XWayland sockets live.
        var info = Info(path, scratch, display: null);
        foreach (var arg in new[] { "-displayfd", "1", "-screen", "0", "1280x800x24", "-nolisten", "tcp", "-noreset" }) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new InvalidOperationException("Xvfb did not start");
    }

    private static async Task<string> ReadDisplayAsync(Process xvfb)
    {
        using var timeout = new CancellationTokenSource(StartupTimeout);
        var line = await xvfb.StandardOutput.ReadLineAsync(timeout.Token);
        if (int.TryParse(line, out var number) is false)
        {
            var error = await xvfb.StandardError.ReadToEndAsync(timeout.Token);
            throw new InvalidOperationException($"Xvfb did not report a display (said '{line}'): {error}");
        }
        return ":" + number;
    }

    private static Process StartHost(string display, string scratch)
    {
        var info = Info("dotnet", scratch, display);
        info.RedirectStandardInput = true;
        info.ArgumentList.Add(TestHostDll());
        return Process.Start(info) ?? throw new InvalidOperationException("The test host did not start");
    }

    /// <summary>An empty environment plus the few variables the child needs. No WAYLAND_DISPLAY, no inherited DISPLAY, a bus address that goes nowhere.</summary>
    private static ProcessStartInfo Info(string fileName, string scratch, string? display)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment.Clear();
        info.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin";
        info.Environment["HOME"] = scratch;
        foreach (var name in new[] { "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_STATE_HOME", "XDG_CACHE_HOME", "XDG_RUNTIME_DIR" })
        {
            var dir = Path.Combine(scratch, name.ToLowerInvariant());
            Directory.CreateDirectory(dir);
            info.Environment[name] = dir;
        }
        info.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/nonexistent";
        if (display is not null) info.Environment["DISPLAY"] = display;

        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string key && key.StartsWith("DOTNET_", StringComparison.Ordinal) && variable.Value is string value)
                info.Environment[key] = value;
        }
        return info;
    }

    /// <summary>The host is built through a ProjectReference without referencing its assembly, so its output sits beside this project's under tests/.</summary>
    private static string TestHostDll()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var helper = Path.Combine(dir.FullName, "SilverModManager.App.UI.X11.TestHost", "bin");
            if (!Directory.Exists(helper)) continue;

            var dll = Directory.EnumerateFiles(helper, "SilverModManager.App.UI.X11.TestHost.dll", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (dll is not null) return dll;
        }
        throw new FileNotFoundException($"SilverModManager.App.UI.X11.TestHost.dll not found; build tests/SilverModManager.App.UI.X11.TestHost");
    }

    private static string? FindOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, program))
            .FirstOrDefault(File.Exists);

    private static void Kill(Process? process)
    {
        if (process is null) return;
        try
        {
            if (process.HasExited is false) process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
    }
}
