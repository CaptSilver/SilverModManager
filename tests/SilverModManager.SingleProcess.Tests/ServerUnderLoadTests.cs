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
/// Stops the thread pool from ever running anything else (every worker it may have is held on a gate), then checks that
/// the server still greets a client. A main in the middle of a big download batch is in this state: its workers are
/// all busy, and a greeting that needs one of them is a main that looks hung.
/// </summary>
[Collection(Name)]
public sealed class ServerUnderLoadTests : IDisposable
{
    public const string Name = "Thread pool saturation";

    private readonly string _directory = Directory.CreateTempSubdirectory("server-load-tests").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void TheGreetingAndTheQuestionArriveWhileEveryThreadPoolWorkerIsBusy()
    {
        var syncPath = FileSystem.Shared.FromUnsanitizedFullPath(Path.Combine(_directory, "test.sync"));
        var services = new ServiceCollection().AddSingleton(typeof(ILogger<>), typeof(NullLogger<>)).BuildServiceProvider();
        var server = new CliServer(
            NullLogger<CliServer>.Instance, new CommandLineConfigurator(services, [], []), Substitute.For<ISettingsManager>(),
            new SyncFile(NullLogger<SyncFile>.Instance, syncPath), services
        );
        server.StartCliServerAsync().GetAwaiter().GetResult();
        var port = new SyncFile(NullLogger<SyncFile>.Instance, syncPath).GetSyncInfo().Port;

        using var gate = new ManualResetEventSlim();
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        ThreadPool.GetMaxThreads(out var oldMaxWorkers, out var oldMaxIo);
        try
        {
            ThreadPool.SetMaxThreads(minWorkers, oldMaxIo).Should().BeTrue();

            // More than the pool can ever run: the surplus stays queued, and queued work is all a new request gets.
            for (var i = 0; i < minWorkers * 2; i++) ThreadPool.QueueUserWorkItem(_ => gate.Wait());
            SpinWait.SpinUntil(() => ThreadPool.PendingWorkItemCount > 0, TimeSpan.FromSeconds(10)).Should().BeTrue("the pool has to be full");

            // A thread of its own, so that the client does not need the pool it is checking on.
            IMessage? first = null, second = null;
            Exception? failure = null;
            var client = new Thread(() =>
            {
                try
                {
                    using var tcp = new TcpClient();
                    tcp.Connect(IPAddress.Loopback, port);
                    tcp.ReceiveTimeout = 3000;
                    var stream = tcp.GetStream();
                    first = ReadFrame(stream);
                    second = ReadFrame(stream);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            }) { IsBackground = true };
            client.Start();
            client.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();

            failure.Should().BeNull();
            first.Should().BeOfType<VersionHello>();
            second.Should().BeOfType<ProgramArgumentsRequest>();
        }
        finally
        {
            gate.Set();
            ThreadPool.SetMaxThreads(oldMaxWorkers, oldMaxIo);
            server.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            server.Dispose();
        }
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

[CollectionDefinition(ServerUnderLoadTests.Name, DisableParallelization = true)]
public sealed class ThreadPoolSaturationCollection;
