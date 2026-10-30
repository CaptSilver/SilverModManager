using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Settings;
using Polly;

namespace SilverModManager.Networking.HttpDownloader;

public static class Services
{
    /// <summary>
    /// Add the default HTTP downloader services
    /// </summary>
    public static IServiceCollection AddHttpDownloader(this IServiceCollection services)
    {
        return services
            .AddSettings<HttpProxySettings>()
            .AddSingleton<HttpClient>(_ =>
            {
                var client = BuildClient();
                return client;
            });
    }

    /// <summary>
    /// The handler every download goes through. Proxy settings still apply: they come from <see cref="HttpClient.DefaultProxy"/>,
    /// and the connect callback is handed the proxy's address when there is one.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        // Without this a host that never completes the TCP handshake holds each request for the full
        // HttpClient timeout (100 s). Vortex uses the same 30 s.
        ConnectTimeout = TimeSpan.FromSeconds(30),
        // Connections that sat unused in the pool are the ones a NAT, VPN or proxy has forgotten about by now.
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectCallback = ConnectWithKeepAlive,
    };

    private static async ValueTask<Stream> ConnectWithKeepAlive(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            ConfigureKeepAlive(socket);
            await socket.ConnectAsync(context.DnsEndPoint, token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Makes the OS probe a quiet connection after 30 s and drop it after three missed probes 10 s apart, so a peer that
    /// vanished fails the read instead of leaving it waiting.
    /// </summary>
    internal static void ConfigureKeepAlive(Socket socket)
    {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10);
        socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
    }

    private static HttpClient BuildClient()
    {
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions
            {
                BackoffType = DelayBackoffType.Exponential,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(3),
                UseJitter = true,
                // A host that doesn't connect within the timeout won't on the next try either; retrying would only
                // multiply the wait. Everything else transient keeps the default handling.
                ShouldHandle = args => ValueTask.FromResult(
                    HttpClientResiliencePredicates.IsTransient(args.Outcome) && !HttpDownloadJob.HasConnectTimeout(args.Outcome.Exception)
                ),
            })
            .Build();

        HttpMessageHandler handler = new ResilienceHandler(pipeline)
        {
            InnerHandler = CreateHandler(),
        };

        var client = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };

        client.DefaultRequestHeaders.UserAgent.Add(ApplicationConstants.UserAgent);

        return client;
    }
}
