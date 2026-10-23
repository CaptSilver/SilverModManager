using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.SingleProcess.Exceptions;
using NSubstitute;
using FluentAssertions;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.SingleProcess.Tests;

public sealed class SyncFileKindTests : IDisposable
{
    // Above the kernel's pid_max, so no process can ever have it.
    private const int DeadPid = 0x7FFFFFF0;

    private readonly string _directory = Directory.CreateTempSubdirectory("sync-file-tests").FullName;

    private AbsolutePath SyncPath => FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_directory, "test.sync"));

    private SyncFile Create() => new(NullLogger<SyncFile>.Instance, SyncPath);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void TrySetMainWritesPidKindAndPortAsOneWord()
    {
        var syncFile = Create();

        syncFile.TrySetMain(MainKind.HeadlessRun, 0x1234).Should().BeTrue();

        syncFile.RawMainWord.Should().Be(((ulong)Environment.ProcessId << 32) | (1UL << 16) | 0x1234UL);
    }

    [Theory]
    [InlineData(MainKind.Gui)]
    [InlineData(MainKind.HeadlessRun)]
    [InlineData(MainKind.HeadlessCli)]
    public void KindIsReadBack(MainKind kind)
    {
        var syncFile = Create();
        syncFile.TrySetMain(kind, 4321).Should().BeTrue();

        var (process, readKind, port) = syncFile.GetSyncInfo();

        process!.Id.Should().Be(Environment.ProcessId);
        readKind.Should().Be(kind);
        port.Should().Be(4321);
    }

    [Fact]
    public void PortIsMaskedToSixteenBits()
    {
        var syncFile = Create();
        syncFile.TrySetMain(MainKind.HeadlessCli, 0xFFFF).Should().BeTrue();

        var (_, kind, port) = syncFile.GetSyncInfo();

        kind.Should().Be(MainKind.HeadlessCli);
        port.Should().Be(0xFFFF);
    }

    [Fact]
    public void PortAboveSixteenBitsIsRejected()
    {
        var syncFile = Create();

        var act = () => syncFile.TrySetMain(MainKind.Gui, 0x10000);

        act.Should().Throw<ArgumentOutOfRangeException>();
        syncFile.RawMainWord.Should().Be(0);
    }

    [Theory]
    [InlineData(MainKind.Gui)]
    [InlineData(MainKind.HeadlessRun)]
    [InlineData(MainKind.HeadlessCli)]
    public void DeadPidReadsAsNoMainWhateverTheKind(MainKind kind)
    {
        var syncFile = Create();
        PlantWord(((ulong)DeadPid << 32) | ((ulong)kind << 16) | 99UL);

        syncFile.GetSyncInfo().Process.Should().BeNull();
        syncFile.IsMainRunning.Should().BeFalse();
    }

    [Fact]
    public void DeadPidDoesNotBlockANewMain()
    {
        var syncFile = Create();
        PlantWord(((ulong)DeadPid << 32) | (1UL << 16) | 99UL);

        syncFile.TrySetMain(MainKind.Gui, 5000).Should().BeTrue();

        syncFile.GetSyncInfo().Should().Match<(System.Diagnostics.Process? Process, MainKind Kind, int Port)>(
            info => info.Process!.Id == Environment.ProcessId && info.Kind == MainKind.Gui && info.Port == 5000
        );
    }

    [Fact]
    public void LiveForeignMainBlocksTrySetMain()
    {
        var syncFile = Create();
        // pid 1 is always alive and is never this process
        var foreign = (1UL << 32) | (2UL << 16) | 7UL;
        PlantWord(foreign);

        syncFile.TrySetMain(MainKind.Gui, 5000).Should().BeFalse();

        syncFile.RawMainWord.Should().Be(foreign);
    }

    [Fact]
    public void ReleaseMainClearsOwnEntry()
    {
        var syncFile = Create();
        syncFile.TrySetMain(MainKind.HeadlessRun, 0).Should().BeTrue();

        syncFile.ReleaseMain().Should().BeTrue();

        syncFile.RawMainWord.Should().Be(0);
        syncFile.IsMainRunning.Should().BeFalse();
    }

    [Fact]
    public void ReleaseMainLeavesAnotherPidsEntryAlone()
    {
        var syncFile = Create();
        var foreign = (1UL << 32) | (1UL << 16) | 7UL;
        PlantWord(foreign);

        syncFile.ReleaseMain().Should().BeFalse();

        syncFile.RawMainWord.Should().Be(foreign);
    }

    [Fact]
    public void ReleaseMainOnEmptyFileIsANoOp()
    {
        Create().ReleaseMain().Should().BeFalse();
    }

    [Fact]
    public async Task ClientRefusesHeadlessMainBeforeConnecting()
    {
        var syncFile = Create();
        // A listener that would record any connection attempt.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            syncFile.TrySetMain(MainKind.HeadlessRun, port).Should().BeTrue();
            var client = new CliClient(NullLogger<CliClient>.Instance, syncFile, Substitute.For<IServiceProvider>());

            var act = () => client.ExecuteCommand(["some-verb"]);

            var thrown = (await act.Should().ThrowAsync<HeadlessMainRunning>()).Which;
            thrown.Kind.Should().Be(MainKind.HeadlessRun);
            thrown.ProcessId.Should().Be(Environment.ProcessId);
            thrown.Should().NotBeAssignableTo<NoMainProcessStarted>();
            listener.Pending().Should().BeFalse();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ClientStillReportsNoMainWhenNothingIsRunning()
    {
        var client = new CliClient(NullLogger<CliClient>.Instance, Create(), Substitute.For<IServiceProvider>());

        var act = () => client.ExecuteCommand(["some-verb"]);

        await act.Should().ThrowAsync<NoMainProcessStarted>();
    }

    [Theory]
    [InlineData(MainKind.HeadlessRun, "A game is running through SilverModManager")]
    [InlineData(MainKind.HeadlessCli, "busy in another terminal (pid 42)")]
    public void ExceptionMessageNamesTheKind(MainKind kind, string expected)
    {
        new HeadlessMainRunning(kind, 42).Message.Should().Contain(expected).And.Contain("42");
    }

    [Fact]
    public async Task CliServerClaimsGuiWithItsRealPort()
    {
        var syncFile = Create();
        var services = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider();
        var configurator = new CommandLineConfigurator(services, [], []);
        using var server = new CliServer(
            NullLogger<CliServer>.Instance, configurator, Substitute.For<ISettingsManager>(), syncFile, services
        );

        await server.StartCliServerAsync();

        var (process, kind, port) = syncFile.GetSyncInfo();
        process!.Id.Should().Be(Environment.ProcessId);
        kind.Should().Be(MainKind.Gui);
        port.Should().BeGreaterThan(0);
        ((syncFile.RawMainWord >> 16) & 0xFFFF).Should().Be(0);
        (syncFile.RawMainWord & 0xFFFF).Should().Be((ulong)port);
        await server.StopAsync(CancellationToken.None);
    }

    private void PlantWord(ulong word)
    {
        using var array = new MultiProcessSharedArray(SyncPath, itemCount: 8);
        array.CompareAndSwap(0, 0, word).Should().BeTrue();
    }
}
