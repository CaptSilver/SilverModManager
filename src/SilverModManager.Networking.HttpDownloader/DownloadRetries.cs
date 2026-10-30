using Microsoft.Extensions.Logging;

namespace SilverModManager.Networking.HttpDownloader;

/// <summary>A failure that asking again cannot fix, so <see cref="DownloadRetries"/> rethrows <see cref="Error"/> at once.</summary>
public sealed class PermanentFailureException(Exception error) : Exception(error.Message, error)
{
    public Exception Error { get; } = error;
}

/// <summary>
/// Implemented by a failure that tells the caller how long to wait before asking again, such as a 429 with a
/// <c>Retry-After</c> header. <see cref="DownloadRetries"/> waits at least that long.
/// </summary>
public interface IRetryAfter
{
    TimeSpan RetryAfter { get; }
}

/// <summary>
/// Asks again, with a growing pause, when a request fails for a reason that may pass: a busy host, a 5xx, a dropped
/// connection. A failure wrapped in <see cref="PermanentFailureException"/> and the user cancelling are not retried.
/// </summary>
public static class DownloadRetries
{
    public static async Task<T> Run<T>(ILogger logger, string what, int attempts, TimeSpan firstDelay, Func<Task<T>> request, CancellationToken token, TimeProvider? time = null)
    {
        var delay = firstDelay;
        for (var attempt = 1;; attempt++)
        {
            try
            {
                return await request();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (PermanentFailureException e)
            {
                throw e.Error;
            }
            catch (Exception e) when (attempt < attempts)
            {
                logger.LogWarning("Fetching {What} failed ({Message}), attempt {Attempt} of {Attempts}, waiting {Delay}", what, e.Message, attempt, attempts, delay);
                var wait = e is IRetryAfter hint && hint.RetryAfter > delay ? hint.RetryAfter : delay;
                await Task.Delay(wait, time ?? TimeProvider.System, token);
                delay *= 2;
            }
        }
    }
}
