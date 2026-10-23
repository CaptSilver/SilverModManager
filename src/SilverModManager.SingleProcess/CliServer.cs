using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.Settings;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.ProxyConsole;
using SilverModManager.ProxyConsole.Messages;
using SilverModManager.SingleProcess.Exceptions;

namespace SilverModManager.SingleProcess;

/// <summary>
/// A long-running service that listens for incoming connections from clients and executes them as if they ran
/// on as CLI command.
/// </summary>
public sealed class CliServer : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private CancellationToken Token => _cancellationTokenSource.Token;

    private bool _started;

    private TcpListener? _tcpListener;
    private string? _launchToken;
    private readonly ConcurrentDictionary<Guid, Task> _runningClients = [];

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CliServer> _logger;
    private readonly CommandLineConfigurator _configurator;
    private readonly SyncFile _syncFile;

    /// <summary>
    /// Constructor.
    /// </summary>
    public CliServer(
        ILogger<CliServer> logger,
        CommandLineConfigurator configurator,
        ISettingsManager settingsManager,
        SyncFile syncFile,
        IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configurator = configurator;
        _syncFile = syncFile;

        settingsManager.Get<CliSettings>();
    }
    
    /// <summary>
    /// Starts the CLI server, listening for incoming connections.
    /// This method needs to be called explicitly to start the server.
    /// </summary>
    public async Task StartCliServerAsync()
    {
        if (!_started)
        {
            _started = true;
            await StartTcpListenerAsync();
        }
    }

    private Task StartTcpListenerAsync()
    {
        // Before the port is published, so a client that finds the port also finds the token.
        _launchToken = LaunchToken.Create(_syncFile.TokenPath);

        _tcpListener = new TcpListener(IPAddress.Loopback, 0);
        _tcpListener.Start();
        Task.Run(async () => await StartListeningAsync(), _cancellationTokenSource.Token);
        var port = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;

        if (!_syncFile.TrySetMain(MainKind.Gui, port))
        {
            _logger.LogError("Failed to set main process in shared array, another process is likely running");
            throw new SingleProcessLockException();
        }

        _logger.LogInformation("Started TCP listener on port {Port}", port);
        return Task.CompletedTask;
    }

    private async Task StartListeningAsync()
    {
        while (_started && !_cancellationTokenSource.IsCancellationRequested)
        {
            try
            {
                var found = await _tcpListener!.AcceptTcpClientAsync(Token);
                found.NoDelay = true; // Disable Nagle's algorithm to reduce delay.

                var id = Guid.NewGuid();
                var task = Task.Run(() => HandleClientAsync(id, found), Token);
                _ = _runningClients.GetOrAdd(id, task);

                _logger.LogInformation("Accepted TCP connection from {RemoteEndPoint}", ((IPEndPoint)found.Client.RemoteEndPoint!).Port);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("TCP listener was cancelled, stopping");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Got an exception while accepting a client connection");
            }
        }
    }

    /// <summary>
    /// Handle a client connection
    /// </summary>
    private async Task HandleClientAsync(Guid id, TcpClient client)
    {
        try
        {
            var stream = client.GetStream();

            var (reply, renderer, serializer) = await ProxiedRenderer.Accept(_serviceProvider, stream);
            switch (reply)
            {
                case ProgramArgumentsResponse arguments:
                    await _configurator.RunAsync(arguments.Arguments ?? [], renderer, Token);
                    break;
                case LaunchRequest launch:
                    await RunForwardedLaunchAsync(serializer, launch);
                    break;
                default:
                    _logger.LogWarning("Dropping a client that answered with {Message}", reply?.GetType().Name ?? "nothing");
                    break;
            }
        }
        finally
        {
            client.Dispose();
            _runningClients.Remove(id, out _);
        }
    }

    /// <summary>
    /// Runs a game launch for a <c>run</c> process: asks the handler, tells the client whether it was taken, and
    /// reports how it ended. A stop request, a client that goes away and the server shutting down all stop the launch.
    /// </summary>
    private async Task RunForwardedLaunchAsync(Serializer serializer, LaunchRequest request)
    {
        if (_launchToken is null || !LaunchToken.Matches(_launchToken, request.Token))
        {
            _logger.LogWarning("Refusing a launch request that did not present the launch token");
            await TrySendAsync(serializer, new LaunchDeclined { Reason = "the launch did not present this SilverModManager's launch token" });
            return;
        }

        var handler = _serviceProvider.GetService<IForwardedLaunchHandler>();
        if (handler is null)
        {
            await TrySendAsync(serializer, new LaunchDeclined { Reason = "this SilverModManager cannot run a game launch" });
            return;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        void RequestStop()
        {
            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The launch is over and so is anything left to stop.
            }
        }

        // The client says stop with a message, and a client that vanished (killed, or its session ended) has nobody
        // left to wait for the game, so both end the launch.
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    if (await serializer.ReceiveAsync() is LaunchStop)
                    {
                        _logger.LogInformation("The client of a forwarded launch asked to stop it");
                        RequestStop();
                    }
                }
            }
            catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException or MemoryPackSerializationException)
            {
                _logger.LogInformation("The client of a forwarded launch is gone");
                RequestStop();
            }
        }, CancellationToken.None);

        ForwardedLaunchAdmission admission;
        try
        {
            admission = await handler.AdmitAsync(new ForwardedLaunch(request.LoadoutName, request.Game, request.Command, request.Environment, request.WorkingDirectory));
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Deciding whether to run a forwarded launch failed");
            await TrySendAsync(serializer, new LaunchDeclined { Reason = $"the launch failed: {e.Message}" });
            return;
        }

        if (admission.DeclineReason is { } reason)
        {
            await TrySendAsync(serializer, new LaunchDeclined { Reason = reason });
            return;
        }

        await TrySendAsync(serializer, new LaunchAccepted());

        ForwardedLaunchExit exit;
        try
        {
            exit = await admission.RunAsync(stop.Token);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "A forwarded launch failed");
            exit = new ForwardedLaunchExit(LaunchFailureExitCode, $"The launch failed: {e.Message}");
        }

        await TrySendAsync(serializer, new LaunchExited { ExitCode = exit.ExitCode, Message = exit.Message });
    }

    /// <summary>The exit code of a launch that failed, as <c>run</c> reports it.</summary>
    private const int LaunchFailureExitCode = 125;

    private async Task TrySendAsync(Serializer serializer, IMessage message)
    {
        try
        {
            await serializer.SendAsync(message);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            _logger.LogInformation(e, "Could not send {Message}: the client is gone", message.GetType().Name);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_started) return; 

        // Ditch this value and don't wait on it because it otherwise blocks the shutdown even when *no-one* is 
        // waiting on the token
        _ = _cancellationTokenSource.CancelAsync();

        _tcpListener?.Stop();
        await Task.WhenAll(_runningClients.Values.ToArray());
        _started = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        _cancellationTokenSource.Dispose();
        _tcpListener?.Dispose();
    }
}
