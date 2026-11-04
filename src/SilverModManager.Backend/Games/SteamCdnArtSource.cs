using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Backend.Games;

/// <summary>
/// Fetches cover art from Steam's public image servers for a game whose art Steam has not cached on this
/// machine. Each image is kept in the app's own cache folder, so it is downloaded once; nothing is bundled.
/// </summary>
/// <remarks>
/// URL shapes, checked against the live server for a handful of ids (a missing asset answers 404 text/html):
/// <list type="bullet">
/// <item><c>https://cdn.cloudflare.steamstatic.com/steam/apps/&lt;appid&gt;/library_600x900.jpg</c> is the portrait cover.</item>
/// <item><c>.../header.jpg</c> is a 460x215 landscape banner; it is the fallback for a game without a cover.</item>
/// <item><c>.../library_capsule.jpg</c> answered 404 for every id tried, so it is not requested.</item>
/// <item>The 32x32 client icon is at <c>steamcommunity/public/images/apps/&lt;appid&gt;/&lt;hash&gt;.jpg</c>, and the hash
/// is only exposed through the keyed Web API, so the icon slot uses the cover, as it does when Steam's cache has no icon.</item>
/// </list>
/// A 404 is remembered in a <c>.missing</c> marker for a week. Any other failure (timeout, 5xx, a body that
/// is not an image) is only remembered in memory for a few minutes, so a bad network never costs a request per read.
/// </remarks>
internal sealed class SteamCdnArtSource : IGameArtProvider
{
    private const string BaseUrl = "https://cdn.cloudflare.steamstatic.com/steam/apps/";
    private const string CoverFile = "library_600x900.jpg";
    private const string BannerFile = "header.jpg";
    private const string MarkerSuffix = ".missing";
    private const int MaxImageBytes = 8 * 1024 * 1024;

    private static readonly string[] TileFiles = [CoverFile, BannerFile];

    // A landscape banner in a square slot is a worse picture than the generated initials.
    private static readonly string[] IconFiles = [CoverFile];

    private static readonly TimeSpan MarkerLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(5);

    private readonly Func<AbsolutePath> _cacheRoot;
    private readonly HttpClient _client;
    private readonly ILogger<SteamCdnArtSource> _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, Lazy<Task<Outcome>>> _inFlight = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _backoff = new();

    public SteamCdnArtSource(IFileSystem fileSystem, HttpClient client, ILogger<SteamCdnArtSource> logger)
        : this(
            () => fileSystem.GetKnownPath(KnownPath.XDG_CACHE_HOME).Combine(ApplicationConstants.DataDirectoryName).Combine("GameArt/Steam"),
            client, logger, TimeProvider.System, TimeSpan.FromSeconds(10), maxConcurrency: 4) { }

    internal SteamCdnArtSource(Func<AbsolutePath> cacheRoot, HttpClient client, ILogger<SteamCdnArtSource> logger, TimeProvider clock, TimeSpan requestTimeout, int maxConcurrency)
    {
        _cacheRoot = cacheRoot;
        _client = client;
        _logger = logger;
        _clock = clock;
        _requestTimeout = requestTimeout;
        _slots = new SemaphoreSlim(maxConcurrency);
    }

    public ValueTask<IStreamFactory?> FindTileAsync(GameArtRequest request) => Find(request, TileFiles);

    public ValueTask<IStreamFactory?> FindIconAsync(GameArtRequest request) => Find(request, IconFiles);

    private async ValueTask<IStreamFactory?> Find(GameArtRequest request, string[] files)
    {
        foreach (var appId in request.SteamAppIds)
        {
            foreach (var file in files)
            {
                var outcome = await Obtain(appId, file).ConfigureAwait(false);
                if (outcome.Path.HasValue) return new NativeFileStreamFactory(outcome.Path.Value);

                // A failure says nothing about the next file, and taking the banner because the cover
                // download hiccuped would pin the worse picture in the cache.
                if (outcome.Failed) break;
            }
        }

        return null;
    }

    private async Task<Outcome> Obtain(uint appId, string fileName)
    {
        var path = _cacheRoot().Combine(appId.ToString(CultureInfo.InvariantCulture)).Combine(fileName);
        if (await IsValidCachedImage(path).ConfigureAwait(false)) return Outcome.Found(path);

        var marker = path.Parent.Combine(fileName + MarkerSuffix);
        if (await MarkerIsFresh(marker).ConfigureAwait(false)) return Outcome.Missing;

        var key = $"{appId}/{fileName}";
        if (_backoff.TryGetValue(key, out var until))
        {
            if (_clock.GetUtcNow() < until) return Outcome.Failure;
            _backoff.TryRemove(key, out _);
        }

        // Two tiles asking for one game at once share a single download.
        var download = _inFlight.GetOrAdd(key, _ => new Lazy<Task<Outcome>>(() => Download(appId, fileName, path, marker)));
        try
        {
            return await download.Value.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<Outcome>>>(key, download));
        }
    }

    private async Task<Outcome> Download(uint appId, string fileName, AbsolutePath path, AbsolutePath marker)
    {
        var key = $"{appId}/{fileName}";
        var url = $"{BaseUrl}{appId}/{fileName}";
        try
        {
            await _slots.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(_requestTimeout);
                using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogDebug("Steam has no {File} for app {AppId}", fileName, appId);
                    await WriteMarker(marker).ConfigureAwait(false);
                    return Outcome.Missing;
                }

                if (!response.IsSuccessStatusCode) return Fail(key, $"HTTP {(int)response.StatusCode}");

                if (response.Content.Headers.ContentLength > MaxImageBytes) return Fail(key, "the image is larger than expected");

                var bytes = await ReadAtMost(response.Content, MaxImageBytes, timeout.Token).ConfigureAwait(false);
                if (bytes is null) return Fail(key, "the image is larger than expected");
                if (!LooksLikeAnImage(bytes)) return Fail(key, "the body is not a JPEG or PNG");

                await WriteAtomically(path, bytes).ConfigureAwait(false);
                _logger.LogInformation("Downloaded {File} for Steam app {AppId} into the art cache", fileName, appId);
                return Outcome.Found(path);
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            return Fail(key, exception.GetType().Name + (exception is OperationCanceledException ? " (timed out)" : ": " + exception.Message));
        }
    }

    /// <summary>
    /// The body, or null once it passes <paramref name="limit"/>. A chunked reply carries no length to check up front,
    /// so the copy itself stops at the limit instead of buffering whatever the server sends.
    /// </summary>
    private static async Task<byte[]?> ReadAtMost(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        await using var body = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (copy.Length + read > limit) return null;
            copy.Write(buffer, 0, read);
        }

        return copy.ToArray();
    }

    private Outcome Fail(string key, string reason)
    {
        _logger.LogInformation("Could not fetch Steam art {Key}: {Reason}", key, reason);
        _backoff[key] = _clock.GetUtcNow() + FailureBackoff;
        return Outcome.Failure;
    }

    private static async Task<bool> IsValidCachedImage(AbsolutePath path)
    {
        if (!path.FileExists) return false;

        var header = new byte[8];
        try
        {
            await using var stream = path.Read();
            var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false).ConfigureAwait(false);
            if (LooksLikeAnImage(header.AsSpan(0, read))) return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        // Not an image: something else wrote here. Drop it so a fresh download can take its place.
        try
        {
            path.Delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private static bool LooksLikeAnImage(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> jpeg = [0xFF, 0xD8, 0xFF];
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        return bytes.StartsWith(jpeg) || bytes.StartsWith(png);
    }

    private static async Task WriteAtomically(AbsolutePath path, byte[] bytes)
    {
        path.Parent.CreateDirectory();
        var temp = path.Parent.Combine($"{path.FileName}.tmp-{Guid.NewGuid():N}");
        try
        {
            await temp.WriteAllBytesAsync(bytes).ConfigureAwait(false);
            temp.FileSystem.MoveFile(temp, path, overwrite: true);
        }
        finally
        {
            if (temp.FileExists) temp.Delete();
        }
    }

    private async Task WriteMarker(AbsolutePath marker)
    {
        marker.Parent.CreateDirectory();
        await marker.WriteAllTextAsync(_clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
    }

    private async Task<bool> MarkerIsFresh(AbsolutePath marker)
    {
        if (!marker.FileExists) return false;

        try
        {
            var text = await marker.ReadAllTextAsync().ConfigureAwait(false);
            return long.TryParse(text, CultureInfo.InvariantCulture, out var seconds)
                   && _clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds) < MarkerLifetime;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private readonly record struct Outcome(AbsolutePath? Path, bool Failed)
    {
        public static readonly Outcome Missing = new(null, false);
        public static readonly Outcome Failure = new(null, true);

        public static Outcome Found(AbsolutePath path) => new(path, false);
    }
}
