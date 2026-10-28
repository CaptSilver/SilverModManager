using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using SilverModManager.Sdk;
using Polly;

namespace SilverModManager.Networking.HttpDownloader;

public static class Services
{
    /// <summary>
    /// Add the default HTTP downloader services
    /// </summary>
    public static IServiceCollection AddHttpDownloader(this IServiceCollection services)
    {
        return services.AddSingleton<HttpClient>(_ =>
        {
            var client = BuildClient();
            return client;
        });
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
            InnerHandler = new SocketsHttpHandler
            {
                // Without this a host that never completes the TCP handshake holds each request for the full
                // HttpClient timeout (100 s). Vortex uses the same 30 s.
                ConnectTimeout = TimeSpan.FromSeconds(30),
            },
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
