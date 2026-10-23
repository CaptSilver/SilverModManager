using System.Diagnostics;

namespace NexusMods.Secrets.SecretService.Tests.Integration;

/// <summary>
/// A session bus with no service directories, so nothing owns <c>org.freedesktop.secrets</c> and nothing can
/// be activated: the state of a KDE login before kwalletd6 has brought up ksecretd.
/// </summary>
public sealed class EmptyBus : IAsyncDisposable
{
    private const string Config = """
        <!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN"
         "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
        <busconfig>
          <type>session</type>
          <listen>unix:dir={0}</listen>
          <policy context="default">
            <allow send_destination="*" eavesdrop="true"/>
            <allow eavesdrop="true"/>
            <allow own="*"/>
          </policy>
        </busconfig>
        """;

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("smss-bus-");
    private Process? _daemon;

    public string? SkipReason { get; private set; }

    public string Address { get; private set; } = "";

    public async Task StartAsync()
    {
        if (PrivateSecretService.FindOnPath("dbus-daemon") is null)
        {
            SkipReason = "dbus-daemon is not installed; the Secret Service integration tests need it (dnf install dbus-daemon).";
            return;
        }

        var config = Path.Combine(_root.FullName, "bus.conf");
        await File.WriteAllTextAsync(config, string.Format(Config, _root.FullName));

        _daemon = Process.Start(new ProcessStartInfo("dbus-daemon")
        {
            ArgumentList = { $"--config-file={config}", "--nofork", "--print-address=1" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("dbus-daemon did not start");

        Address = (await _daemon.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))?.Trim()
            ?? throw new InvalidOperationException("dbus-daemon exited without printing an address");
    }

    public async ValueTask DisposeAsync()
    {
        if (_daemon is not null)
        {
            if (!_daemon.HasExited) _daemon.Kill();
            await _daemon.WaitForExitAsync();
            _daemon.Dispose();
        }
        _root.Delete(recursive: true);
    }
}
