using System.Net;
using System.Net.Sockets;
using FluentAssertions;

namespace SilverModManager.Networking.HttpDownloader.Tests;

public class DownloadHandlerTests
{
    [Fact]
    public void TheHandler_ProbesIdleConnectionsAndDoesNotKeepOldOnesPooled()
    {
        using var handler = Services.CreateHandler();

        handler.ConnectCallback.Should().NotBeNull("sockets are opened by hand so keepalive can be set");
        handler.PooledConnectionIdleTimeout.Should().BeLessThan(TimeSpan.FromMinutes(5));
        handler.PooledConnectionLifetime.Should().BeLessThan(TimeSpan.FromHours(1));
        handler.ConnectTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void KeepAlive_TurnsOnProbingWithShortIntervals()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        Services.ConfigureKeepAlive(socket);

        socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive).Should().Be(1);
        ((int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)).Should().BeInRange(1, 60);
        ((int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)).Should().BeInRange(1, 30);
        ((int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)).Should().BeInRange(1, 10);
    }

    [Fact]
    public async Task ARequestThroughTheHandler_ReachesALoopbackServer()
    {
        using var listener = new HttpListener();
        var port = Random.Shared.Next(49152, 65535);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            await context.Response.OutputStream.WriteAsync("hello"u8.ToArray());
            context.Response.Close();
        });

        using var handler = Services.CreateHandler();
        using var client = new HttpClient(handler);
        var text = await client.GetStringAsync($"http://127.0.0.1:{port}/", TestContext.Current.CancellationToken);
        await serving;

        text.Should().Be("hello");
    }
}
