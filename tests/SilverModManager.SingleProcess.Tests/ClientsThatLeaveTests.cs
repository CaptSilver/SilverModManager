using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.ProxyConsole.Messages;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.SingleProcess.Tests;

/// <summary>
/// A client that reads the greeting and hangs up has its handler finish, and dispose the connection, on the thread
/// pool while the accept thread may still be logging it. Anything the accept thread reads from the connection after
/// the hand-off throws on a thread nobody catches, and that takes the whole app down. Timing decides whether a single
/// client hits it, so this sends a burst and then checks that the server is still alive and greeting.
/// </summary>
public sealed class ClientsThatLeaveTests : IDisposable
{
    private const int Burst = 200;

    private readonly string _directory = Directory.CreateTempSubdirectory("clients-that-leave").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ABurstOfClientsThatHangUpAfterTheGreeting_LeavesTheServerGreeting()
    {
        var syncPath = FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_directory, "test.sync"));
        var services = new ServiceCollection().AddSingleton(typeof(ILogger<>), typeof(NullLogger<>)).BuildServiceProvider();
        var server = new CliServer(
            NullLogger<CliServer>.Instance, new CommandLineConfigurator(services, [], []), Substitute.For<ISettingsManager>(),
            new SyncFile(NullLogger<SyncFile>.Instance, syncPath), services
        );
        server.StartCliServerAsync().GetAwaiter().GetResult();
        try
        {
            var port = new SyncFile(NullLogger<SyncFile>.Instance, syncPath).GetSyncInfo().Port;

            for (var i = 0; i < Burst; i++)
            {
                using var tcp = Connect(port);
                ReadFrame(tcp.GetStream()).Should().BeOfType<VersionHello>();
            }

            using var last = Connect(port);
            var stream = last.GetStream();
            ReadFrame(stream).Should().BeOfType<VersionHello>();
            ReadFrame(stream).Should().BeOfType<ProgramArgumentsRequest>();
        }
        finally
        {
            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            server.Dispose();
        }
    }

    private static TcpClient Connect(int port)
    {
        var tcp = new TcpClient();
        tcp.Connect(IPAddress.Loopback, port);
        tcp.ReceiveTimeout = 3000;
        return tcp;
    }

    private static IMessage? ReadFrame(NetworkStream stream)
    {
        var header = new byte[4];
        stream.ReadExactly(header);
        var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        stream.ReadExactly(body);
        return MemoryPackSerializer.Deserialize<IMessage>(body);
    }
}
