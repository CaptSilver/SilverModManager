using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using DynamicData.Kernel;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.HttpDownloads;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using NexusMods.Paths;
using Polly;
using Polly.Retry;
using System.ComponentModel;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.Networking.HttpDownloader;

/// <summary>
/// Download job.
/// </summary>
[PublicAPI]
public record HttpDownloadJob : IJobDefinitionWithStart<HttpDownloadJob, AbsolutePath>, IHttpDownloadJob
{
    private static readonly ResiliencePipeline<AbsolutePath> ResiliencePipeline = BuildResiliencePipeline();

    /// <summary>
    /// Client.
    /// </summary>
    public required HttpClient Client { get; init; }

    /// <summary>
    /// Logger.
    /// </summary>
    public required ILogger Logger { get; init; }

    /// <summary>
    /// The uri of the download page.
    /// </summary>
    public required Uri Uri { get; init; }
    
    /// <summary>
    /// The uri of the download page.
    /// </summary>
    public required Uri DownloadPageUri { get; init; }
    
    /// <summary>
    /// The destination of the download.
    /// </summary>
    public required AbsolutePath Destination { get; init; }
    
    /// <summary>
    /// Only exists for extension by derived classes.
    /// </summary>
    public virtual ValueTask AddMetadata(ITransaction transaction, LibraryFile.New libraryFile)
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// How long the body may send nothing before the connection is treated as dead and the rest of the file is asked for again.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = IdleReads.DefaultTimeout;

    /// <summary>
    /// How many times in a row a silent connection is asked again without any new bytes arriving before the job fails.
    /// </summary>
    public int MaxIdleRetries { get; init; } = 3;

    /// <summary>
    /// The clock the idle limit runs on.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    private readonly HttpDownloadState _state = new();
    
    public bool SupportsPausing => true;
    /// <summary>
    /// Implementation for external job state data access
    /// </summary>
    public IPublicJobStateData? GetJobStateData() => _state;
    
    /// <summary>
    /// Constructor for the job
    /// </summary>
    public static IJobTask<HttpDownloadJob, AbsolutePath> Create(
        IServiceProvider provider,
        Uri uri,
        Uri downloadPage,
        AbsolutePath destination)
    {
        var monitor = provider.GetRequiredService<IJobMonitor>();
        var job = new HttpDownloadJob
        {
            Uri = uri,
            DownloadPageUri = downloadPage,
            Destination = destination,
            Logger = provider.GetRequiredService<ILogger<HttpDownloadJob>>(),
            Client = provider.GetRequiredService<HttpClient>(),
        };

        return monitor.Begin<HttpDownloadJob, AbsolutePath>(job);
    }

    /// <inheritdoc/>
    public async ValueTask<AbsolutePath> StartAsync(IJobContext<HttpDownloadJob> context)
    {
        // A stalled connection fails the attempt like any dropped one, but asking again straight away is enough:
        // the wait was already the idle limit. Only stalls that bring no new bytes count towards giving up.
        var stallsWithoutProgress = 0;
        while (true)
        {
            var before = _state.TotalBytesDownloaded;
            try
            {
                return await ResiliencePipeline.ExecuteAsync(
                    callback: static (tuple, _) =>
                    {
                        var (self, context) = tuple;
                        return self.StartAsyncImpl(context);
                    },
                    state: (this, context),
                    cancellationToken: context.CancellationToken
                );
            }
            catch (DownloadStalledException) when (!context.JobCancellationToken.IsPaused && !context.JobCancellationToken.IsCancelled)
            {
                if (_state.TotalBytesDownloaded > before) stallsWithoutProgress = 0;
                if (++stallsWithoutProgress > MaxIdleRetries) throw;
            }
        }
    }

    private async ValueTask<AbsolutePath> StartAsyncImpl(IJobContext<HttpDownloadJob> context)
    {
        await context.YieldAsync();
        if (_state.TotalBytesDownloaded > Size.Zero)
            Logger.LogInformation("Resuming download from {Bytes} bytes", _state.TotalBytesDownloaded);
        
        Logger.LogDebug("Requesting headers of `{Uri}`", Uri.GetLeftPart(UriPartial.Path));
        await WithHostTimeoutReported(() => FetchMetadata(context).AsTask(), context);
        if (!_state.AcceptRanges.Value && _state.TotalBytesDownloaded > Size.Zero)
        {
            // Without ranges the only way to ask again is for the whole file, which then has to overwrite from the start.
            Logger.LogInformation("The server does not support ranges, restarting the download from the beginning");
            _state.TotalBytesDownloaded = Size.Zero;
        }
        Logger.LogDebug("Downloading `{Uri}`, {Length} bytes", Uri.GetLeftPart(UriPartial.Path), _state.ContentLength);

        await context.YieldAsync();
        await using var fileStream = Destination.Open(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        fileStream.Position = (long)_state.TotalBytesDownloaded.Value;
        
        await using var outputStream = new StreamProgressWrapper<IJobContext<HttpDownloadJob>>(
            fileStream,
            context,
            (state, tuple) =>
            {
                var (bytesWritten, speed) = tuple;

                _state.TotalBytesDownloaded = bytesWritten;

                state.SetPercent(bytesWritten, _state.ContentLength.ValueOr(static () => Size.One));
                state.SetRateOfProgress(speed);
            }
        );

        if (_state.ContentLength.HasValue)
        {
            var contentLength = (long)_state.ContentLength.Value.Value;
            if (outputStream.Length != contentLength) outputStream.SetLength(contentLength);
        }

        await context.YieldAsync();
        using var request = PrepareRequest(out var isRangeRequest);
        using var response = await WithHostTimeoutReported(() => Client.SendAsync(
            request: request,
            completionOption: HttpCompletionOption.ResponseHeadersRead,
            cancellationToken: context.CancellationToken
        ), context);

        response.EnsureSuccessStatusCode();

        if (response.StatusCode == HttpStatusCode.OK)
        {
            // NOTE(erri120): We might've not gotten the content length in the initial HEAD request.
            if (!_state.ContentLength.HasValue)
            {
                // For full requests only, the response should contain the content length at this point.
                // For range requests, the content length equals the length of the range, so we can't use that here.
                var newContentLength = response.Content.Headers.ContentLength;
                if (newContentLength is not null)
                {
                    _state.ContentLength = Size.FromLong(newContentLength.Value);
                    outputStream.SetLength(newContentLength.Value);
                }
            }
        } else if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            if (!_state.ContentLength.HasValue)
            {
                // Responses to range requests should have this header
                // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Content-Range
                var contentRange = response.Content.Headers.ContentRange;
                var length = contentRange?.Length;
                if (length is not null)
                {
                    _state.ContentLength = Size.FromLong(length.Value);
                    outputStream.SetLength(length.Value);
                }
            }
        }

        if (isRangeRequest && response.StatusCode == HttpStatusCode.OK)
        {
            // NOTE(erri120): We asked the server whether it supports range requests, the server responded with yes,
            // then we do a range request, and suddenly the server changed its mind and says no...
            Logger.LogWarning("Server `{ServerName}` responded with 200 to a valid range request for download from `{PageUri}`. The download will be reset", response.Headers.Server.ToString(), DownloadPageUri);

            // NOTE(erri120): The only thing we can do here is to reset everything and start from scratch.
            _state.TotalBytesDownloaded = Size.Zero;
            outputStream.Position = 0;
            _state.AcceptRanges = false;
        }

        try
        {
            await CopyBody(response, outputStream, context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DownloadStalledException)
        {
            // Already logged where it was raised, and the caller asks again.
            throw;
        }
        catch (Exception e)
        {
            Logger.LogWarning(e, "Exception while downloading from `{PageUri}`, downloaded `{DownloadedBytes}` from `{TotalBytes}` bytes", DownloadPageUri, outputStream.Position, outputStream.Length);
            throw;
        }
        finally
        {
            _state.TotalBytesDownloaded = Size.FromLong(outputStream.Position);
        }

        // Ensure progress is set to 100% when download completes
        if (_state.ContentLength.HasValue)
            context.SetPercent(_state.ContentLength.Value, _state.ContentLength.Value);
        else
            context.SetPercent(Size.One, Size.One);

        return Destination;
    }
    
    /// <summary>
    /// Copies the body in 1 MB blocks, each read bounded by <see cref="IdleTimeout"/>. A plain CopyToAsync waits on a
    /// connection that has died without a reset for as long as the process lives.
    /// </summary>
    private async Task CopyBody(HttpResponseMessage response, Stream destination, CancellationToken token)
    {
        await using var body = await response.Content.ReadAsStreamAsync(token);
        var buffer = GC.AllocateUninitializedArray<byte>(HashingLimits.ReadBlockSize);
        try
        {
            int read;
            while ((read = await IdleReads.ReadAsync(body, buffer, Uri.Authority, IdleTimeout, TimeProvider, token)) > 0)
                await destination.WriteAsync(buffer.AsMemory(0, read), token);
        }
        catch (DownloadStalledException)
        {
            Logger.LogWarning("`{Host}` sent no data for {Idle}, downloaded `{DownloadedBytes}` from `{TotalBytes}` bytes", Uri.Authority, IdleTimeout, destination.Position, destination.Length);
            throw;
        }
    }

    /// <summary>
    /// HttpClient reports its own timeout as a cancellation, and a connect timeout as a plain network error. Left alone
    /// the first looks like the user cancelled and the second gets retried several times over, so both become a
    /// <see cref="HostNotRespondingException"/> that names the host and is not retried.
    /// </summary>
    private async Task<T> WithHostTimeoutReported<T>(Func<Task<T>> request, IJobContext context)
    {
        try
        {
            return await request();
        }
        catch (Exception e) when (IsTimeout(e, context))
        {
            throw new HostNotRespondingException(Uri.Authority, e);
        }
    }

    private async Task WithHostTimeoutReported(Func<Task> request, IJobContext context)
    {
        await WithHostTimeoutReported(async () =>
        {
            await request();
            return true;
        }, context);
    }

    private static bool IsTimeout(Exception exception, IJobContext context)
    {
        // Pausing cancels the token and then swaps in a fresh one, so by the time this runs the token itself no longer
        // says the job was interrupted. The job's own state does.
        if (context.JobCancellationToken.IsPaused || context.JobCancellationToken.IsCancelled) return false;
        return HasConnectTimeout(exception);
    }

    internal static bool HasConnectTimeout(Exception? exception)
    {
        if (TimeoutDetection.HasTimeoutInChain(exception)) return true;
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is SocketException { SocketErrorCode: SocketError.TimedOut }) return true;
        }
        return false;
    }

    private HttpRequestMessage PrepareRequest(out bool isRangeRequest)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Uri);

        // NOTE(erri120): use a normal GET request for the entire file
        if (!_state.AcceptRanges.Value || _state.TotalBytesDownloaded == Size.Zero)
        {
            // NOTE(erri120): Using If-Match to ensure that what we're downloading didn't suddenly change
            if (_state.ETag.HasValue)
            {
                // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/If-Match
                request.Headers.IfMatch.Add(_state.ETag.Value);
            }

            isRangeRequest = false;
        }
        else
        {
            // NOTE(erri120): Using If-Range for range requests instead of If-Match
            if (_state.ETag.HasValue)
            {
                // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/If-Range
                request.Headers.IfRange = new RangeConditionHeaderValue(_state.ETag.Value);
            }

            // A server MAY send a Content-Length header field in a response to a HEAD request
            // https://httpwg.org/specs/rfc9110.html#rfc.section.8.6

            // NOTE(erri120): As such, we might not know the content length, but since we download
            // in serial, we can omit the end position to request all remaining bytes
            var range = _state.ContentLength.HasValue
                ? new RangeHeaderValue(
                    from: (long)_state.TotalBytesDownloaded.Value,
                    to: (long)_state.ContentLength.Value.Value
                )
                : new RangeHeaderValue(
                    from: (long)_state.TotalBytesDownloaded.Value,
                    to: null
                );

            // https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Range
            request.Headers.Range = range;
            isRangeRequest = true;
        }

        return request;
    }

    private async ValueTask FetchMetadata(IJobContext context)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, Uri);
        using var response = await Client.SendAsync(
            request: request,
            completionOption: HttpCompletionOption.ResponseHeadersRead,
            cancellationToken: context.CancellationToken
        ).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        // https://datatracker.ietf.org/doc/html/rfc7233#section-5.1.2
        // "bytes" and "none" are the only range units we care about,
        // "none" meaning the server doesn't support ranges
        _state.AcceptRanges = response.Headers.AcceptRanges.Contains("bytes");
        _state.ETag = response.Headers.ETag;

        var contentLength = response.Content.Headers.ContentLength;
        _state.ContentLength = contentLength is not null ? Size.FromLong(contentLength.Value) : Optional<Size>.None;
    }

    private static ResiliencePipeline<AbsolutePath> BuildResiliencePipeline()
    {
        ImmutableArray<Type> networkExceptions =
        [
            typeof(HttpIOException),
            typeof(HttpRequestException),
            typeof(SocketException),
        ];

        var pipeline = new ResiliencePipelineBuilder<AbsolutePath>()
            .AddRetry(new RetryStrategyOptions<AbsolutePath>
            {
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is not null && networkExceptions.Contains(args.Outcome.Exception.GetType())),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(3),
            })
            .Build();

        return pipeline;
    }
}

/// <summary>
/// The host of a download did not answer in time. Deliberately not a network error the retry pipeline recognises:
/// a host that never connects is not going to start after a few seconds.
/// </summary>
public class HostNotRespondingException(string host, Exception inner) : UserFacingException($"{host} didn't respond", inner)
{
    public override string Title => "The download server didn't respond";

    /// <summary>
    /// The host (and port, when not the default) that did not respond, without the path or query of the request.
    /// </summary>
    public string Host { get; } = host;
}

/// <summary>
/// Public interface for external access to download information
/// </summary>
[PublicAPI]
public interface IHttpDownloadState : IPublicJobStateData, INotifyPropertyChanged
{
    /// <summary>
    /// Content length from HTTP headers
    /// </summary>
    Optional<Size> ContentLength { get; }
    
    /// <summary>
    /// Total bytes downloaded so far
    /// </summary>
    Size TotalBytesDownloaded { get; }
}

/// <summary>
/// Internal mutable state that persists across pause/resume cycles
/// </summary>
internal sealed partial class HttpDownloadState : ReactiveObject, IHttpDownloadState
{
    [Reactive] public partial Optional<Size> ContentLength { get; set; }
    [Reactive] public partial Size TotalBytesDownloaded { get; set; }
    public Optional<EntityTagHeaderValue> ETag { get; set; }
    public Optional<bool> AcceptRanges { get; set; }
}
