using NexusMods.Paths;

namespace SilverModManager.Platform.Proton.PrefixSetup;

/// <summary>
/// Downloads a file for a prefix component. It does not check the contents: the caller verifies the hash.
/// </summary>
public interface IPrefixFileFetcher
{
    /// <summary>
    /// Saves what <paramref name="url"/> returns at <paramref name="destination"/>, replacing any file there.
    /// </summary>
    /// <exception cref="IOException">The download failed.</exception>
    Task Fetch(string url, AbsolutePath destination, CancellationToken cancellationToken);
}

/// <inheritdoc/>
internal sealed class HttpPrefixFileFetcher(HttpClient client) : IPrefixFileFetcher
{
    public async Task Fetch(string url, AbsolutePath destination, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            destination.Parent.CreateDirectory();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = destination.Create();
            await source.CopyToAsync(target, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new IOException($"Downloading {url} failed: {e.Message}", e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient's own timeout; only the caller's token means the user cancelled.
            throw new IOException($"Downloading {url} failed: {e.Message}", e);
        }
    }
}
