using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SilverModManager.ProxyConsole;
using SilverModManager.ProxyConsole.Messages;
using SilverModManager.Sdk.ProxyConsole;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.SingleProcess.Exceptions;
using Spectre.Console;

namespace SilverModManager.SingleProcess;

/// <summary>
/// Client helper for connecting to a CliServer and executing commands
/// </summary>
public class CliClient(
    ILogger<CliClient> logger,
    SyncFile syncFile,
    IServiceProvider provider,
    AppBuild? build = null,
    TimeSpan? greetingTimeout = null)
{
    private static readonly TimeSpan DefaultGreetingTimeout = TimeSpan.FromSeconds(10);

    private readonly AppBuild _build = build ?? AppBuild.Current;
    private readonly TimeSpan _greetingTimeout = greetingTimeout ?? DefaultGreetingTimeout;

    /// <summary>
    /// Starts the client process, connecting to the main process and running the console response loop. Returns the
    /// exit code the verb finished with in the main process.
    /// </summary>
    public async Task<int> ExecuteCommand(string[] args, IAnsiConsole? console = null)
    {
        var result = await ExecuteCommandCoreAsync(args, new SpectreRenderer(console ?? AnsiConsole.Console), CancellationToken.None);

        // A main that never reported a code (an older build, or one that went away) leaves the success it always was.
        return result.ExitCode ?? 0;
    }

    /// <summary>
    /// Runs a command in the main process, rendering its output to <paramref name="renderer"/>. Unlike the terminal
    /// overload, a connection that closes before the verb ends is reported as a result with no exit code rather than
    /// as success, and <paramref name="token"/> abandons the wait (the command may still be running in the main
    /// process).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was cancelled before the command ended.</exception>
    public Task<CommandResult> ExecuteCommand(string[] args, IRenderer renderer, CancellationToken token) =>
        ExecuteCommandCoreAsync(args, renderer, token);

    private async Task<CommandResult> ExecuteCommandCoreAsync(string[] args, IRenderer renderer, CancellationToken token)
    {
        var (process, kind, port) = syncFile.GetSyncInfo();
        if (process is null)
        {
            throw new NoMainProcessStarted();
        }

        // A headless main has no server to connect to, and starting another main would
        // fight it for the game and the database.
        if (kind != MainKind.Gui)
        {
            throw new HeadlessMainRunning(kind, process.Id);
        }

        logger.LogInformation("Found main process {ProcessId} listening on port {Port}", process.Id, port);

        using var client = new TcpClient();
        client.NoDelay = true; // Disable Nagle's algorithm to reduce delay.
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, token);
            await using var stream = client.GetStream();

            logger.LogDebug("Connected to main process {ProcessId} on port {Port}", process.Id, port);
            await CheckGreetingAsync(new Serializer(stream, provider.GetRequiredService<IEnumerable<IRenderableDefinition>>()), token);
            return await RunTillCloseAsync(new ConsoleSettings
            {
                Arguments = args,
                Renderer = renderer,
            }, stream, token);
        }
        catch (SocketException)
        {
            logger.LogWarning("Failed to connect to main process {ProcessId} on port {Port}", process.Id, port);
            throw;
        }
    }

    /// <summary>
    /// Asks the main process to run a game launch and waits for it to end. <paramref name="stop"/> asks the main
    /// process to end the game; the outcome then comes back as usual, with the exit code of a stopped launch.
    /// </summary>
    /// <remarks>
    /// Never throws for a main that cannot be asked: that is <see cref="ForwardedLaunchOutcome.Unavailable"/>, so the
    /// caller can refuse the launch in words rather than crash.
    /// </remarks>
    public async Task<ForwardedLaunchOutcome> ForwardLaunchAsync(ForwardedLaunch launch, CancellationToken stop)
    {
        var (process, kind, port) = syncFile.GetSyncInfo();
        if (process is null) throw new NoMainProcessStarted();
        if (kind != MainKind.Gui) throw new HeadlessMainRunning(kind, process.Id);

        var token = LaunchToken.Read(syncFile.TokenPath);
        if (token is null) return new ForwardedLaunchOutcome.Unavailable("the running SilverModManager left no launch token");

        using var client = new TcpClient();
        client.NoDelay = true;
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
        }
        catch (SocketException e)
        {
            logger.LogWarning(e, "Failed to connect to main process {ProcessId} on port {Port}", process.Id, port);
            return new ForwardedLaunchOutcome.Unavailable($"could not connect to the running SilverModManager ({e.Message})");
        }

        var serializer = new Serializer(client.GetStream(), provider.GetRequiredService<IEnumerable<IRenderableDefinition>>());
        var accepted = false;
        try
        {
            await CheckGreetingAsync(serializer, CancellationToken.None);
            if (await serializer.ReceiveAsync() is not ProgramArgumentsRequest)
                return new ForwardedLaunchOutcome.Unavailable("the running SilverModManager did not greet this client the way it expects");

            await serializer.SendAsync(new LaunchRequest
            {
                Token = token,
                LoadoutName = launch.LoadoutName,
                Game = launch.Game,
                Command = [..launch.Command],
                Environment = new Dictionary<string, string>(launch.Environment),
                WorkingDirectory = launch.WorkingDirectory,
            });

            // The stop is sent from whichever thread cancels; the receive loop below only reads.
            using var stopRegistration = stop.Register(() => _ = SendStopAsync(serializer));

            while (true)
            {
                switch (await serializer.ReceiveAsync())
                {
                    case LaunchDeclined declined:
                        return new ForwardedLaunchOutcome.Declined(declined.Reason);
                    case LaunchAccepted:
                        accepted = true;
                        break;
                    case LaunchExited exited:
                        return new ForwardedLaunchOutcome.Exited(exited.ExitCode, exited.Message);
                    default:
                        return new ForwardedLaunchOutcome.Unavailable("the running SilverModManager answered a launch with something this client does not know");
                }
            }
        }
        catch (Exception e) when (e is VersionMismatch or MainProcessSilent)
        {
            return new ForwardedLaunchOutcome.Unavailable(e.Message);
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or MemoryPack.MemoryPackSerializationException)
        {
            logger.LogWarning(e, "Lost the connection to main process {ProcessId} during a forwarded launch", process.Id);

            // Before the main process accepted, nothing ran: it may simply be a build that does not know the request.
            // After, the game may still be running with nobody to stop it.
            return accepted
                ? new ForwardedLaunchOutcome.Exited(LaunchFailureExitCode, "SilverModManager closed the connection before the game ended; the game may still be running.")
                : new ForwardedLaunchOutcome.Unavailable("the running SilverModManager closed the connection without answering; it is probably an older build that cannot take a launch");
        }
    }

    private const int LaunchFailureExitCode = 125;

    private const string Remedy = "Close the app and run the command again, or run the same version.";

    /// <summary>
    /// Reads the main process's greeting and refuses to go on unless it is the same build as this one. Nothing has
    /// been sent by then, so a refused command never reaches the main process.
    /// </summary>
    /// <exception cref="VersionMismatch">The main process is another build, or an older one that sends no greeting.</exception>
    /// <exception cref="MainProcessSilent">The main process said nothing within the greeting timeout.</exception>
    private async Task CheckGreetingAsync(Serializer serializer, CancellationToken token)
    {
        var receive = serializer.ReceiveAsync();
        IMessage? greeting;
        try
        {
            greeting = await receive.WaitAsync(_greetingTimeout, token);
        }
        catch (TimeoutException)
        {
            // The abandoned read ends with the connection; nobody is left to look at its failure.
            _ = receive.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            throw new MainProcessSilent($"The running SilverModManager did not say which version it is. It may be a different version or stuck. {Remedy}");
        }

        switch (greeting)
        {
            case VersionHello hello when AppBuild.From(hello) == _build:
                return;
            case VersionHello hello:
                var running = AppBuild.From(hello);
                throw new VersionMismatch($"The running SilverModManager is {running.Describe(_build)} but this command is {_build.Describe(running)}. {Remedy}");
            default:
                // A build from before the greeting opens with its question straight away.
                throw new VersionMismatch($"The running SilverModManager is an older version than this command ({_build.Version}). {Remedy}");
        }
    }

    private async Task SendStopAsync(Serializer serializer)
    {
        try
        {
            await serializer.SendAsync(new LaunchStop());
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The connection is already gone; the main process stops the launch when it sees that.
            logger.LogDebug(e, "Could not send the stop request");
        }
    }

    private async Task<CommandResult> RunTillCloseAsync(ConsoleSettings proxy, NetworkStream stream, CancellationToken token)
    {
        try
        {
            var adaptor = new ClientRendererAdaptor(stream, proxy.Renderer, provider, proxy.Arguments);

            // The adaptor has no token of its own; closing the stream under it is what ends its read loop.
            await using var abandon = token.Register(stream.Close);
            try
            {
                await adaptor.RunningTask;
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }

            // Closing the stream can end the read loop quietly; that is still a cancellation, not a closed connection.
            if (adaptor.ExitCode is null) token.ThrowIfCancellationRequested();

            return new CommandResult(adaptor.ExitCode);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Client disconnected");
            return new CommandResult(null);
        }
    }
}
