using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using NexusMods.App.UI.Helpers;
using Tmds.DBus.Protocol;

namespace NexusMods.UI.Tests;

/// <summary>
/// Avalonia opens one process-wide D-Bus connection and subscribes to it from the UI thread, so
/// every subscription remembers the UI synchronization context. When that connection drops
/// after the dispatcher has stopped, Tmds.DBus delivers the disconnect to the remembered
/// context from a thread-pool thread, which throws and takes the process down. These tests run
/// against a private dbus-daemon so nothing touches the real session bus.
/// </summary>
public sealed class AvaloniaDBusTests : IAsyncLifetime
{
    private const string DBusHelperType = "Avalonia.FreeDesktop.DBusHelper, Avalonia.FreeDesktop";
    private const string DefaultConnectionField = "s_defaultConntection";

    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory("silver-dbus-");
    private Process? _daemon;
    private Connection? _connection;
    private object? _originalDefaultConnection;

    private static FieldInfo DefaultConnectionFieldInfo =>
        Type.GetType(DBusHelperType, throwOnError: true)!
            .GetField(DefaultConnectionField, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException(DBusHelperType, DefaultConnectionField);

    public async ValueTask InitializeAsync()
    {
        _daemon = Process.Start(new ProcessStartInfo("dbus-daemon")
        {
            ArgumentList = { "--session", "--nofork", "--print-address=1", $"--address=unix:path={Path.Combine(_tempDir.FullName, "bus")}" },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("dbus-daemon did not start");

        var address = await _daemon.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        address.Should().NotBeNullOrEmpty("dbus-daemon prints the address it listens on");

        _connection = new Connection(new ClientConnectionOptions(address!) { AutoConnect = false });
        await _connection.ConnectAsync();

        // Stand in for the connection Avalonia.FreeDesktop would have opened itself.
        _originalDefaultConnection = DefaultConnectionFieldInfo.GetValue(null);
        DefaultConnectionFieldInfo.SetValue(null, _connection);
    }

    public ValueTask DisposeAsync()
    {
        DefaultConnectionFieldInfo.SetValue(null, _originalDefaultConnection);
        _connection?.Dispose();
        if (_daemon is { HasExited: false }) _daemon.Kill();
        _daemon?.Dispose();
        _tempDir.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task DroppedConnection_DeliversDisconnectToCapturedContext_WhenNotDisposedFirst()
    {
        // Documents the failure the helper prevents, and proves the harness can provoke it.
        var context = await SubscribeFromContext();

        context.StopDispatching();
        _daemon!.Kill();
        await _daemon.WaitForExitAsync(TestContext.Current.CancellationToken);

        var delivered = await Task.WhenAny(context.SendAfterStop.Task, Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        (delivered == context.SendAfterStop.Task)
            .Should().BeTrue("the dropped connection reports to the context from a thread-pool thread");
    }

    [Fact]
    public async Task DisposeDefaultConnection_PreventsDisconnectDeliveryAfterDispatcherStops()
    {
        var context = await SubscribeFromContext();

        AvaloniaDBus.DisposeDefaultConnection();

        context.StopDispatching();
        _daemon!.Kill();
        await _daemon.WaitForExitAsync(TestContext.Current.CancellationToken);

        // Negative check: give a late disconnect callback time to arrive.
        var delivered = await Task.WhenAny(context.SendAfterStop.Task, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        (delivered == context.SendAfterStop.Task)
            .Should().BeFalse("the connection was already disposed while the context could still run it");
    }

    [Fact]
    public void DisposeDefaultConnection_IsNoOp_WhenAvaloniaNeverOpenedAConnection()
    {
        DefaultConnectionFieldInfo.SetValue(null, null);

        var act = () => AvaloniaDBus.DisposeDefaultConnection();

        act.Should().NotThrow();
    }

    private async Task<StoppableContext> SubscribeFromContext()
    {
        var context = new StoppableContext();
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Interface = "org.freedesktop.DBus",
            Member = "NameOwnerChanged",
        };

        await _connection!.AddMatchAsync<int>(
            rule,
            static (_, _) => 0,
            static (_, _, _, _) => { },
            readerState: null,
            handlerState: null,
            context,
            ObserverFlags.EmitOnDispose
        );
        return context;
    }

    /// <summary>
    /// Runs callbacks until <see cref="StopDispatching"/>, then behaves like a stopped
    /// Avalonia dispatcher and records every attempt to use it. The real dispatcher throws
    /// TaskCanceledException from Send at that point; throwing here would take the test host down.
    /// </summary>
    private sealed class StoppableContext : SynchronizationContext
    {
        private volatile bool _stopped;
        public TaskCompletionSource SendAfterStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void StopDispatching() => _stopped = true;

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (_stopped)
            {
                SendAfterStop.TrySetResult();
                return;
            }
            d(state);
        }
    }
}
