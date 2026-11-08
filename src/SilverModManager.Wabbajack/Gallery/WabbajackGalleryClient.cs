using System.Reactive.Subjects;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;

namespace SilverModManager.Wabbajack.Gallery;

/// <summary>The combined gallery, and when it was fetched.</summary>
/// <param name="IsStale">The network could not be reached, so this is an older copy than the cache lifetime allows.</param>
/// <param name="FailedRepositories">Repositories that could not be read, so their entries are missing from <paramref name="Entries"/>.</param>
public sealed record GalleryListing(IReadOnlyList<GalleryEntry> Entries, DateTimeOffset FetchedAt, bool IsStale, IReadOnlyList<string>? FailedRepositories = null)
{
    /// <summary>False when a repository is missing, so an entry absent from the listing may only be absent from what loaded.</summary>
    public bool IsComplete => FailedRepositories is not { Count: > 0 };
}

public interface IWabbajackGalleryClient
{
    /// <summary>The combined gallery from the cache or, when that is old or missing, the network.</summary>
    Task<GalleryListing> Get(bool refresh = false, CancellationToken cancellationToken = default);

    /// <summary>The listing in the disk cache, never fetching. Parsed once per cache file version.</summary>
    GalleryListing? GetCached();

    /// <summary>
    /// Emits after every successful network fetch, and only then. It emits on a thread-pool thread, so a UI subscriber
    /// must <c>ObserveOn</c> the UI scheduler. A subscriber that throws is logged and does not affect the fetch.
    /// </summary>
    IObservable<GalleryListing> Refreshed { get; }
}

/// <summary>
/// Reads Wabbajack's public gallery: <c>repositories.json</c> names the repositories, each has a
/// <c>modlists.json</c>. The combined result is cached on disk; a fresh cache is used as is, an old one is replaced
/// when the network answers and served as it is when it doesn't.
/// </summary>
public sealed class WabbajackGalleryClient(HttpClient client, AbsolutePath cacheFile, TimeProvider time, ILogger<WabbajackGalleryClient> logger) : IWabbajackGalleryClient
{
    public static readonly Uri RepositoriesAddress = new("https://raw.githubusercontent.com/wabbajack-tools/mod-lists/master/repositories.json");

    public const int MaxParallelRepositories = 4;

    /// <summary>
    /// The largest document read from a repository. The official list is a few megabytes; anything far past that is
    /// a broken or hostile repository, and it is not worth holding in memory to find out.
    /// </summary>
    public const int MaxDocumentBytes = 32 * 1024 * 1024;
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly Lock _flightLock = new();
    private Task<GalleryListing>? _inFlight;
    private readonly Subject<GalleryListing> _refreshed = new();
    private readonly Lock _cacheLock = new();
    private (DateTime Written, long Length, GalleryListing? Listing)? _parsed;

    public IObservable<GalleryListing> Refreshed => _refreshed;

    public GalleryListing? GetCached()
    {
        lock (_cacheLock)
        {
            try
            {
                if (!cacheFile.FileExists) { _parsed = null; return null; }
                var info = new FileInfo(cacheFile.ToString());
                if (_parsed is { } known && known.Written == info.LastWriteTimeUtc && known.Length == info.Length) return known.Listing;

                // A junk file is remembered too, so an unchanged one is neither re-read nor reported again.
                var listing = LoadCache();
                _parsed = (info.LastWriteTimeUtc, info.Length, listing);
                return listing;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Callers that ask while a fetch is running share it instead of starting their own: the repositories are hit once
    /// and two writers never race on the cache file. The shared fetch is not tied to any one caller's token, which only
    /// stops that caller from waiting; every request in it has its own timeout.
    /// </summary>
    /// <param name="refresh">Fetch even when the cache is still fresh.</param>
    /// <exception cref="GalleryUnavailableException">The network failed and there is no cached copy to fall back on.</exception>
    public Task<GalleryListing> Get(bool refresh = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<GalleryListing> flight;
        lock (_flightLock)
        {
            flight = _inFlight ??= Task.Run(() => GetShared(refresh));
        }
        return flight.WaitAsync(cancellationToken);
    }

    private async Task<GalleryListing> GetShared(bool refresh)
    {
        try
        {
            return await Load(refresh);
        }
        finally
        {
            lock (_flightLock) _inFlight = null;
        }
    }

    private async Task<GalleryListing> Load(bool refresh)
    {
        var cached = GetCached();
        if (!refresh && cached is not null && time.GetUtcNow() - cached.FetchedAt < CacheLifetime) return cached;

        GalleryListing fetched;
        try
        {
            fetched = await Fetch(CancellationToken.None);
        }
        catch (Exception e) when (e is GalleryUnavailableException or GalleryFormatException or HttpRequestException or TaskCanceledException)
        {
            if (cached is null) throw e as GalleryUnavailableException ?? new GalleryUnavailableException("The Wabbajack gallery could not be reached", e);
            logger.LogWarning(e, "The Wabbajack gallery could not be refreshed, using the copy from {FetchedAt}", cached.FetchedAt);
            return cached with { IsStale = true };
        }

        // Outside the try above: the fetch has succeeded and the cache is written, so a subscriber's failure must not
        // turn it into the stale-cache fallback.
        try
        {
            _refreshed.OnNext(fetched);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "A subscriber to the gallery's Refreshed failed");
        }
        return fetched;
    }

    private async Task<GalleryListing> Fetch(CancellationToken cancellationToken)
    {
        var repositories = GalleryReader.ReadRepositories(await GetText(RepositoriesAddress, cancellationToken));
        if (repositories.Count == 0) throw new GalleryUnavailableException("repositories.json lists no repository");

        using var gate = new SemaphoreSlim(MaxParallelRepositories);
        var fetched = await Task.WhenAll(repositories.Select(async repository =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var text = await GetText(repository.Url, cancellationToken);
                // Parsed here so a repository with junk is skipped now instead of poisoning the cache.
                return (repository, text: (string?)text, entries: (IReadOnlyList<GalleryEntry>)GalleryReader.ReadModlists(text, repository.Name));
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested && e is GalleryFormatException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(e, "Skipping gallery repository {Repository}", repository.Name);
                return (repository, text: (string?)null, entries: (IReadOnlyList<GalleryEntry>)[]);
            }
            finally
            {
                gate.Release();
            }
        }));

        var good = fetched.Where(f => f.text is not null).ToList();
        if (good.Count == 0) throw new GalleryUnavailableException("No gallery repository could be read");

        var failed = fetched.Where(f => f.text is null).Select(f => f.repository.Name).ToList();
        var now = time.GetUtcNow();
        SaveCache(now, good.ToDictionary(f => f.repository.Name, f => f.text ?? ""), failed);
        return new GalleryListing(good.SelectMany(f => f.entries).ToList(), now, IsStale: false, failed);
    }

    private async Task<string> GetText(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDocumentBytes) throw TooLarge(uri);

        // The length header is optional and can lie, so the body itself is read no further than the limit.
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, timeout.Token)) > 0)
        {
            if (buffer.Length + read > MaxDocumentBytes) throw TooLarge(uri);
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        // Through a reader, as ReadAsStringAsync would, so a byte-order mark is dropped rather than handed to the parser.
        using var reader = new StreamReader(buffer, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(timeout.Token);
    }

    private static GalleryFormatException TooLarge(Uri uri) => new($"{uri} is larger than {MaxDocumentBytes} bytes");

    // The cache holds the repositories' documents as they arrived and is read back through the same parser, so there
    // is one format to get right and a hand-edited or truncated file is just another kind of junk.
    private sealed record CacheFile(DateTimeOffset FetchedAt, Dictionary<string, string> Repositories, List<string>? Failed = null);

    private GalleryListing? LoadCache()
    {
        try
        {
            if (!cacheFile.FileExists) return null;
            var parsed = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(cacheFile.ToString()));
            if (parsed?.Repositories is null) return null;

            var entries = new List<GalleryEntry>();
            var failed = new List<string>(parsed.Failed ?? []);
            foreach (var (name, text) in parsed.Repositories)
            {
                try { entries.AddRange(GalleryReader.ReadModlists(text, name)); }
                catch (GalleryFormatException) { failed.Add(name); }
            }
            return new GalleryListing(entries, parsed.FetchedAt, IsStale: false, failed);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Ignoring the unreadable gallery cache '{Path}'", cacheFile);
            return null;
        }
    }

    private void SaveCache(DateTimeOffset fetchedAt, Dictionary<string, string> repositories, List<string> failed)
    {
        try
        {
            cacheFile.Parent.CreateDirectory();
            var temporary = cacheFile.Parent.Combine(cacheFile.FileName + ".tmp");
            File.WriteAllText(temporary.ToString(), JsonSerializer.Serialize(new CacheFile(fetchedAt, repositories, failed)));
            File.Move(temporary.ToString(), cacheFile.ToString(), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache that can't be written costs a fetch next time, not the list we already have.
            logger.LogWarning(e, "Could not write the gallery cache '{Path}'", cacheFile);
        }
    }
}
