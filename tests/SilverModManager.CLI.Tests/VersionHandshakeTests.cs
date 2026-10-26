using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Cli;
using SilverModManager.ProxyConsole;
using SilverModManager.ProxyConsole.Messages;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Settings;
using SilverModManager.SingleProcess;
using SilverModManager.SingleProcess.Exceptions;
using Spectre.Console.Testing;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// A command is only sent to a running SilverModManager of the same build: a different build may not speak the same
/// messages, and a refusal in words beats a client that dies on a message it has never heard of.
/// </summary>
public sealed class VersionHandshakeTests : IAsyncLifetime
{
    private const int TimeoutMs = 120_000;

    private static readonly AppBuild Running = new("0.2.0", "aaaaaaaaaaaa");

    private static int _verbRuns;

    private readonly string _directory = Directory.CreateTempSubdirectory("version-handshake-tests").FullName;
    private readonly List<TcpListener> _listeners = [];
    private readonly CancellationTokenSource _fakeMain = new();
    private CliServer? _server;

    public static class TestVerbs
    {
        [Verb("test-handshake-ok", "Counts its runs")]
        public static Task<int> Ok([Injected] IRenderer renderer)
        {
            Interlocked.Increment(ref _verbRuns);
            return Task.FromResult(0);
        }
    }

    private static ServiceProvider Services() => new ServiceCollection()
        .AddLogging()
        .AddDefaultRenderers()
        .AddVerb(() => TestVerbs.Ok)
        .AddSingleton<CommandLineConfigurator>()
        .BuildServiceProvider();

    private SyncFile SyncFile(string name) => new(NullLogger<SyncFile>.Instance, FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_directory, name)));

    public ValueTask InitializeAsync()
    {
        _verbRuns = 0;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.StopAsync(CancellationToken.None);
        _server?.Dispose();
        await _fakeMain.CancelAsync();
        foreach (var listener in _listeners) listener.Stop();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<CliClient> StartMain(AppBuild client, TimeSpan? greetingTimeout = null)
    {
        var services = Services();
        _server = new CliServer(NullLogger<CliServer>.Instance, services.GetRequiredService<CommandLineConfigurator>(), Substitute.For<ISettingsManager>(), SyncFile("test.sync"), services, Running);
        await _server.StartCliServerAsync();
        return new CliClient(NullLogger<CliClient>.Instance, SyncFile("test.sync"), services, client, greetingTimeout);
    }

    /// <summary>
    /// A stand-in main process that runs <paramref name="behaviour"/> on the first connection it gets.
    /// </summary>
    private CliClient StartFakeMain(Func<Serializer, Task> behaviour, TimeSpan greetingTimeout)
    {
        var services = Services();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        SyncFile("fake.sync").TrySetMain(MainKind.Gui, ((IPEndPoint)listener.LocalEndpoint).Port).Should().BeTrue();

        _ = Task.Run(async () =>
        {
            try
            {
                using var connection = await listener.AcceptTcpClientAsync();
                await behaviour(new Serializer(connection.GetStream(), services.GetRequiredService<IEnumerable<IRenderableDefinition>>()));
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or OperationCanceledException)
            {
                // The test ended and tore the listener down.
            }
        });

        return new CliClient(NullLogger<CliClient>.Instance, SyncFile("fake.sync"), services, Running, greetingTimeout);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task MatchingBuilds_RunTheCommand()
    {
        var client = await StartMain(Running);

        var exitCode = await client.ExecuteCommand(["test-handshake-ok"], new TestConsole());

        exitCode.Should().Be(0);
        _verbRuns.Should().Be(1);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ADifferentVersion_IsRefusedInWords_AndTheCommandIsNotSent()
    {
        var client = await StartMain(new AppBuild("0.1.2", "bbbbbbbbbbbb"));

        var act = () => client.ExecuteCommand(["test-handshake-ok"], new TestConsole());

        var refusal = (await act.Should().ThrowAsync<VersionMismatch>()).Which;
        refusal.Message.Should().Be("The running SilverModManager is 0.2.0 but this command is 0.1.2. Close the app and run the command again, or run the same version.");
        CliErrors.ExitCode(refusal).Should().Be(CliErrors.RefusalExitCode);
        CliErrors.Describe(refusal).Should().Be(refusal.Message);
        _verbRuns.Should().Be(0);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task TheSameVersionFromAnotherCommit_IsRefused_NamingTheCommits()
    {
        var client = await StartMain(new AppBuild("0.2.0", "cccccccccccc"));

        var act = () => client.ExecuteCommand(["test-handshake-ok"], new TestConsole());

        var refusal = (await act.Should().ThrowAsync<VersionMismatch>()).Which;
        refusal.Message.Should().Be("The running SilverModManager is 0.2.0 (aaaaaaa) but this command is 0.2.0 (ccccccc). Close the app and run the command again, or run the same version.");
        _verbRuns.Should().Be(0);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AMainThatGreetsWithoutAVersion_IsRefusedAsOlder()
    {
        // What a build from before the version greeting does: asks for the arguments straight away.
        var client = StartFakeMain(serializer => serializer.SendAsync(new ProgramArgumentsRequest()), TimeSpan.FromSeconds(30));

        var act = () => client.ExecuteCommand(["anything"], new TestConsole());

        var refusal = (await act.Should().ThrowAsync<VersionMismatch>()).Which;
        refusal.Message.Should().Be("The running SilverModManager is an older version than this command (0.2.0). Close the app and run the command again, or run the same version.");
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task AMainThatNeverSpeaks_IsRefusedInWords_WhenTheGreetingTimesOut()
    {
        var client = StartFakeMain(_ => Task.Delay(Timeout.Infinite, _fakeMain.Token), TimeSpan.FromMilliseconds(300));

        var act = () => client.ExecuteCommand(["anything"], new TestConsole());

        var refusal = (await act.Should().ThrowAsync<MainProcessSilent>()).Which;
        refusal.Message.Should().Be("The running SilverModManager did not say which version it is. It may be a different version or stuck. Close the app and run the command again, or run the same version.");
        CliErrors.ExitCode(refusal).Should().Be(CliErrors.RefusalExitCode);
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task ADifferentVersion_MakesAForwardedLaunchUnavailable_WithTheSameSentence()
    {
        var client = await StartMain(new AppBuild("0.1.2", null));

        var outcome = await client.ForwardLaunchAsync(new ForwardedLaunch("Loadout", "1", ["game.exe"], new Dictionary<string, string>(), null), CancellationToken.None);

        outcome.Should().BeOfType<ForwardedLaunchOutcome.Unavailable>().Which.Reason.Should().Contain("The running SilverModManager is 0.2.0 but this command is 0.1.2.");
    }

    // Startup kills a main whose heartbeat fails, so a main that answers as another build must count as alive: the
    // command is then refused in words instead of the user's window being closed.
    [Fact(Timeout = TimeoutMs)]
    public async Task TheHeartbeatProbe_CountsAMainOfAnotherBuild_AsReachable()
    {
        var client = await StartMain(new AppBuild("0.1.2", "bbbbbbbbbbbb"));

        (await Probe(client)).Should().BeTrue();
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task TheHeartbeatProbe_CountsAMainThatGreetsWithoutAVersion_AsReachable()
    {
        var client = StartFakeMain(serializer => serializer.SendAsync(new ProgramArgumentsRequest()), TimeSpan.FromSeconds(30));

        (await Probe(client)).Should().BeTrue();
    }

    [Fact(Timeout = TimeoutMs)]
    public async Task TheHeartbeatProbe_CountsAMainThatNeverSpeaks_AsUnreachable()
    {
        var client = StartFakeMain(_ => Task.Delay(Timeout.Infinite, _fakeMain.Token), TimeSpan.FromMilliseconds(300));

        (await Probe(client)).Should().BeFalse();
    }

    private static Task<bool> Probe(CliClient client) => SilverModManager.App.Program.CanConnectToProcess(
        NullLogger.Instance, port: 0, timeout: TimeSpan.FromSeconds(30), new ServiceCollection().AddSingleton(client).BuildServiceProvider());
}
