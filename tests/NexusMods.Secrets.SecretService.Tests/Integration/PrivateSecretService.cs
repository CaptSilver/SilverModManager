using System.Diagnostics;
using Tmds.DBus.Protocol;
using Xunit;

namespace NexusMods.Secrets.SecretService.Tests.Integration;

/// <summary>
/// A gnome-keyring-daemon on a session bus of its own, with a throwaway HOME and runtime dir, so the tests
/// never see the user's keyring. ksecretd is not usable here: it cannot create or unlock a wallet without
/// its graphical prompter.
/// </summary>
/// <remarks>
/// Everything the bus activates inherits the environment of <c>dbus-run-session</c>, so HOME and friends
/// are set on that process. The keyring's control socket lives at <c>$XDG_RUNTIME_DIR/keyring/control</c>
/// and unix socket paths top out at 108 bytes, hence the short runtime dir under /tmp.
/// </remarks>
public sealed class PrivateSecretService : IAsyncDisposable
{
    private const string Password = "silvermm-test";
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("smss-home-");
    private readonly DirectoryInfo _runtime = Directory.CreateTempSubdirectory("smss-");
    private Process? _session;

    /// <summary>
    /// Set instead of starting when a binary the fixture needs is missing.
    /// </summary>
    public string? SkipReason { get; private set; }

    public string Address { get; private set; } = "";

    public string HomeDirectory => _root.FullName;

    public async Task StartAsync()
    {
        foreach (var binary in new[] { "dbus-run-session", "gnome-keyring-daemon" })
        {
            if (FindOnPath(binary) is not null) continue;
            SkipReason = $"{binary} is not installed; the Secret Service integration tests need it (dnf install gnome-keyring dbus-daemon).";
            return;
        }

        File.SetUnixFileMode(_runtime.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var addressFile = Path.Combine(_root.FullName, "addr");

        // The password on stdin makes gnome-keyring create the "login" keyring already unlocked.
        var script = $"printf %s {Password} | gnome-keyring-daemon --unlock --components=secrets >/dev/null; " +
                     $"echo \"$DBUS_SESSION_BUS_ADDRESS\" > '{addressFile}'; exec sleep infinity";

        var start = new ProcessStartInfo("setsid")
        {
            ArgumentList = { "dbus-run-session", "--", "sh", "-c", script },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["HOME"] = _root.FullName;
        start.Environment["XDG_RUNTIME_DIR"] = _runtime.FullName;
        start.Environment["XDG_DATA_HOME"] = Path.Combine(_root.FullName, "data");
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(_root.FullName, "config");
        start.Environment["XDG_STATE_HOME"] = Path.Combine(_root.FullName, "state");
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(_root.FullName, "cache");
        start.Environment["QT_QPA_PLATFORM"] = "offscreen";
        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");
        start.Environment.Remove("DBUS_SESSION_BUS_ADDRESS");

        // setsid makes the session its own process group, so one kill reaches the bus and everything under it.
        _session = Process.Start(start) ?? throw new InvalidOperationException("setsid did not start");
        _ = _session.StandardOutput.ReadToEndAsync();
        _ = _session.StandardError.ReadToEndAsync();

        Address = await WaitForAddressAsync(addressFile);
        await WaitForSecretServiceAsync();
    }

    /// <summary>
    /// Kills the bus and every daemon under it, as a logout or a crash would.
    /// </summary>
    public void Kill()
    {
        if (_session is { HasExited: false }) Signal(_session.Id);
        KillStrays();
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        if (_session is not null)
        {
            await _session.WaitForExitAsync().WaitAsync(StartupTimeout);
            _session.Dispose();
        }
        _root.Delete(recursive: true);
        _runtime.Delete(recursive: true);
    }

    private async Task<string> WaitForAddressAsync(string addressFile)
    {
        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_session!.HasExited) throw new InvalidOperationException($"dbus-run-session exited with {_session.ExitCode} before printing an address");
            if (File.Exists(addressFile) && File.ReadAllText(addressFile).Trim() is { Length: > 0 } address) return address;
            await Task.Delay(50);
        }
        throw new TimeoutException("dbus-run-session did not publish a bus address");
    }

    private async Task WaitForSecretServiceAsync()
    {
        var deadline = DateTime.UtcNow + StartupTimeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var connection = new Connection(new ClientConnectionOptions(Address) { AutoConnect = false });
                await connection.ConnectAsync();
                if ((await connection.ListServicesAsync()).Contains("org.freedesktop.secrets")) return;
            }
            catch (Exception e) when (e is ConnectException or DBusException or DisconnectedException)
            {
                last = e;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"org.freedesktop.secrets did not appear on {Address}", last);
    }

    /// <summary>
    /// A daemon that detached from the session's process group would outlive the kill. It is recognisable
    /// by the HOME of this instance in its environment.
    /// </summary>
    private void KillStrays()
    {
        var marker = $"HOME={_root.FullName}\0";
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == Environment.ProcessId) continue;
            try
            {
                if (File.ReadAllText(Path.Combine(dir, "environ")).Contains(marker, StringComparison.Ordinal)) Signal(pid, group: false);
            }
            catch (IOException) { /* exited, or not ours to read */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Signal(int pid, bool group = true)
    {
        using var kill = Process.Start(new ProcessStartInfo("kill") { ArgumentList = { "-KILL", "--", group ? $"-{pid}" : $"{pid}" }, UseShellExecute = false })!;
        kill.WaitForExit();
    }

    internal static string? FindOnPath(string binary) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, binary)).FirstOrDefault(File.Exists);
}

public sealed class PrivateSecretServiceFixture : IAsyncLifetime
{
    public PrivateSecretService Service { get; } = new();

    public ValueTask InitializeAsync() => new(Service.StartAsync());

    public ValueTask DisposeAsync() => Service.DisposeAsync();
}
