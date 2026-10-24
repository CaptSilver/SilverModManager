using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.ProxyConsole;
using SilverModManager.ProxyConsole.Messages;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.SingleProcess.Tests;

/// <summary>
/// A <c>run</c> process handing a game launch to the main process over the CLI server's connection: a real
/// <see cref="CliServer"/> and <see cref="CliClient"/> over loopback, with a fake handler standing in for the launch.
/// </summary>
public sealed class ForwardedLaunchTests : IAsyncLifetime
{
    // Every test here waits on a peer in the same process; a lost reply must fail the test by name rather than hold the run.
    private const int TimeoutMs = 120_000;

    private readonly string _directory = Directory.CreateTempSubdirectory("forwarded-launch-tests").FullName;
    private readonly FakeHandler _handler = new();
    private SyncFile _syncFile = null!;
    private CliServer? _server;

    private AbsolutePath SyncPath => FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_directory, "test.sync"));

    public ValueTask InitializeAsync()
    {
        _syncFile = new SyncFile(NullLogger<SyncFile>.Instance, SyncPath);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.StopAsync(CancellationToken.None);
        _server?.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static ServiceProvider Services(IForwardedLaunchHandler? handler)
    {
        var services = new ServiceCollection().AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        if (handler is not null) services.AddSingleton(handler);
        return services.BuildServiceProvider();
    }

    private async Task<CliClient> StartMain(bool withHandler = true)
    {
        var services = Services(withHandler ? _handler : null);
        _server = new CliServer(NullLogger<CliServer>.Instance, new CommandLineConfigurator(services, [], []), Substitute.For<ISettingsManager>(), _syncFile, services);
        await _server.StartCliServerAsync();
        return new CliClient(NullLogger<CliClient>.Instance, new SyncFile(NullLogger<SyncFile>.Instance, SyncPath), services);
    }

    private static ForwardedLaunch Launch() => new(
        "My Loadout", "1091500", ["proton", "waitforexitandrun", "game.exe"],
        new Dictionary<string, string> { ["SteamAppId"] = "1091500", ["PATH"] = "/usr/bin" }, "/games/peak");

    [Fact(Timeout = TimeoutMs)]
    public async Task AnAcceptedLaunchRunsInTheMainProcess_AndTheGamesExitCodeComesBack()
    {
        _handler.Run = _ => Task.FromResult(new ForwardedLaunchExit(7));
        var client = await StartMain();

        var outcome = await client.ForwardLaunchAsync(Launch(), CancellationToken.None);

        outcome.Should().Be(new ForwardedLaunchOutcome.Exited(7, null));
        _handler.Seen.Should().NotBeNull();
        _handler.Seen!.LoadoutName.Should().Be("My Loadout");
        _handler.Seen.Game.Should().Be("1091500");
        _handler.Seen.Command.Should().Equal("proton", "waitforexitandrun", "game.exe");
        _handler.Seen.Environment.Should().Equal(Launch().Environment);
        _handler.Seen.WorkingDirectory.Should().Be("/games/peak");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AFailureOfTheLaunchItselfComesBackWithItsMessage()
    {
        _handler.Run = _ => Task.FromResult(new ForwardedLaunchExit(125, "Test Game is already being launched by another SilverModManager process."));
        var client = await StartMain();

        var outcome = await client.ForwardLaunchAsync(Launch(), CancellationToken.None);

        outcome.Should().Be(new ForwardedLaunchOutcome.Exited(125, "Test Game is already being launched by another SilverModManager process."));
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AnExceptionFromTheLaunchIsReportedAsAFailedLaunch_NotALostConnection()
    {
        _handler.Run = _ => throw new InvalidOperationException("no such program");
        var client = await StartMain();

        var outcome = await client.ForwardLaunchAsync(Launch(), CancellationToken.None);

        outcome.Should().BeOfType<ForwardedLaunchOutcome.Exited>().Which.Should().Match<ForwardedLaunchOutcome.Exited>(
            e => e.ExitCode == 125 && e.Message!.Contains("no such program"));
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ADeclinedLaunchComesBackWithTheReason_AndRunsNothing()
    {
        _handler.DeclineReason = "An apply is running for Test Game.";
        var client = await StartMain();

        var outcome = await client.ForwardLaunchAsync(Launch(), CancellationToken.None);

        outcome.Should().Be(new ForwardedLaunchOutcome.Declined("An apply is running for Test Game."));
        _handler.RunCalls.Should().Be(0);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AMainProcessWithNoHandlerDeclines()
    {
        var client = await StartMain(withHandler: false);

        var outcome = await client.ForwardLaunchAsync(Launch(), CancellationToken.None);

        outcome.Should().BeOfType<ForwardedLaunchOutcome.Declined>();
        _handler.Admissions.Should().Be(0);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AskingToStop_ReachesTheLaunch_AndTheStoppedExitCodeComesBack()
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Run = async stop =>
        {
            running.SetResult();
            try { await Task.Delay(Timeout.Infinite, stop); }
            catch (OperationCanceledException) { }
            return new ForwardedLaunchExit(143);
        };
        var client = await StartMain();
        using var stop = new CancellationTokenSource();

        var forwarding = client.ForwardLaunchAsync(Launch(), stop.Token);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(30));
        forwarding.IsCompleted.Should().BeFalse("the game is still running");
        await stop.CancelAsync();
        var outcome = await forwarding.WaitAsync(TimeSpan.FromSeconds(30));

        outcome.Should().Be(new ForwardedLaunchOutcome.Exited(143, null));
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AStopAskedForBeforeTheMainProcessHasAnswered_StillReachesTheLaunch()
    {
        _handler.Run = async stop =>
        {
            try { await Task.Delay(Timeout.Infinite, stop); }
            catch (OperationCanceledException) { }
            return new ForwardedLaunchExit(143);
        };
        var client = await StartMain();

        var outcome = await client.ForwardLaunchAsync(Launch(), new CancellationToken(canceled: true)).WaitAsync(TimeSpan.FromSeconds(30));

        outcome.Should().Be(new ForwardedLaunchOutcome.Exited(143, null));
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AClientThatDisappears_StopsTheLaunch()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Run = async stop =>
        {
            try { await Task.Delay(Timeout.Infinite, stop); }
            catch (OperationCanceledException) { stopped.SetResult(); }
            return new ForwardedLaunchExit(143);
        };
        await StartMain();
        var (_, _, port) = _syncFile.GetSyncInfo();

        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, port);
            var serializer = new Serializer(raw.GetStream(), []);
            (await serializer.ReceiveAsync()).Should().BeOfType<ProgramArgumentsRequest>();
            await serializer.SendAsync(RawRequest(File.ReadAllText(_syncFile.TokenPath.GetFullPath())));
            (await serializer.ReceiveAsync()).Should().BeOfType<LaunchAccepted>();
        }

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ARequestWithoutTheMainProcessesToken_IsDeclinedWithoutAskingTheHandler()
    {
        await StartMain();
        var (_, _, port) = _syncFile.GetSyncInfo();

        using var raw = new TcpClient();
        await raw.ConnectAsync(IPAddress.Loopback, port);
        var serializer = new Serializer(raw.GetStream(), []);
        await serializer.ReceiveAsync();
        await serializer.SendAsync(RawRequest("not the token"));

        (await serializer.ReceiveAsync()).Should().BeOfType<LaunchDeclined>();
        _handler.Admissions.Should().Be(0);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task TheTokenFileIsReadableByTheOwnerOnly()
    {
        await StartMain();

        File.GetUnixFileMode(_syncFile.TokenPath.GetFullPath()).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AMainProcessThatClosesTheConnectionWithoutAnswering_IsUnavailable_SoTheCallerCanSayItIsTooOld()
    {
        // What a build that does not know the request does: it fails to read it and drops the connection.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _syncFile.TrySetMain(MainKind.Gui, port).Should().BeTrue();
        await File.WriteAllTextAsync(_syncFile.TokenPath.GetFullPath(), "token");
        var accepted = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync();
            var serializer = new Serializer(peer.GetStream(), []);
            await serializer.SendAsync(new ProgramArgumentsRequest());
            await serializer.ReceiveAsync();
        });
        var client = new CliClient(NullLogger<CliClient>.Instance, _syncFile, Services(null));

        var outcome = await client.ForwardLaunchAsync(Launch(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        await accepted;

        outcome.Should().BeOfType<ForwardedLaunchOutcome.Unavailable>();
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AMainProcessThatDiesAfterAccepting_IsAFailedLaunch_NotASilentSuccess()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _syncFile.TrySetMain(MainKind.Gui, port).Should().BeTrue();
        await File.WriteAllTextAsync(_syncFile.TokenPath.GetFullPath(), "token");
        var accepted = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync();
            var serializer = new Serializer(peer.GetStream(), []);
            await serializer.SendAsync(new ProgramArgumentsRequest());
            await serializer.ReceiveAsync();
            await serializer.SendAsync(new LaunchAccepted());
        });
        var client = new CliClient(NullLogger<CliClient>.Instance, _syncFile, Services(null));

        var outcome = await client.ForwardLaunchAsync(Launch(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        await accepted;

        outcome.Should().BeOfType<ForwardedLaunchOutcome.Exited>().Which.Should().Match<ForwardedLaunchOutcome.Exited>(
            e => e.ExitCode == 125 && e.Message!.Contains("closed"));
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task WithNoTokenFileTheMainProcessIsUnavailable()
    {
        await StartMain();
        File.Delete(_syncFile.TokenPath.GetFullPath());
        var client = new CliClient(NullLogger<CliClient>.Instance, _syncFile, Services(null));

        var outcome = await client.ForwardLaunchAsync(Launch(), CancellationToken.None);

        outcome.Should().BeOfType<ForwardedLaunchOutcome.Unavailable>();
    }

    private static LaunchRequest RawRequest(string token) => new()
    {
        Token = token,
        LoadoutName = "My Loadout",
        Command = ["game.exe"],
        Environment = new Dictionary<string, string>(),
    };

    private sealed class FakeHandler : IForwardedLaunchHandler
    {
        public ForwardedLaunch? Seen { get; private set; }
        public string? DeclineReason { get; set; }
        public int Admissions { get; private set; }
        public int RunCalls { get; private set; }
        public Func<CancellationToken, Task<ForwardedLaunchExit>> Run { get; set; } = _ => Task.FromResult(new ForwardedLaunchExit(0));

        public Task<ForwardedLaunchAdmission> AdmitAsync(ForwardedLaunch launch)
        {
            Seen = launch;
            Admissions++;
            if (DeclineReason is not null) return Task.FromResult(ForwardedLaunchAdmission.Declined(DeclineReason));
            return Task.FromResult(ForwardedLaunchAdmission.Accepted(stop =>
            {
                RunCalls++;
                return Run(stop);
            }));
        }
    }
}
