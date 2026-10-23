using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SilverModManager.Networking.Thunderstore.DTOs;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.Networking.Thunderstore;

internal sealed class ThunderstoreApi : IThunderstoreApi
{
    private const string Host = "https://thunderstore.io";

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _httpClient;

    public ThunderstoreApi(HttpClient httpClient) => _httpClient = httpClient;

    public Task<PackageDetail?> FetchPackage(ThunderstorePackageRef package, CancellationToken cancellationToken)
        => GetJsonOrNull<PackageDetail>(PackageUri(package), cancellationToken);

    public Task<PackageVersion?> FetchVersion(ThunderstoreVersionRef version, CancellationToken cancellationToken)
        => GetJsonOrNull<PackageVersion>(new Uri($"{PackageUri(version.Package)}{version.Version}/"), cancellationToken);

    public async Task<ListingIndex> FetchListingIndex(string community, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(community)) throw new ArgumentException("A community slug is required", nameof(community));

        var requested = new Uri($"{Host}/c/{Uri.EscapeDataString(community)}/api/v1/package-listing-index/");
        var (bytes, finalUri) = await GetBytes(requested, cancellationToken);

        var (stream, digest) = ChunkReader.OpenVerified(bytes, finalUri);
        await using (stream)
        {
            var chunks = await JsonSerializer.DeserializeAsync<List<Uri>>(stream, JsonOptions, cancellationToken)
                         ?? throw new JsonException($"Listing index {finalUri} is empty");
            return new ListingIndex(new ThunderstoreBlob(finalUri, digest), chunks);
        }
    }

    public async IAsyncEnumerable<PackageListing> ReadChunk(Uri chunk, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (bytes, finalUri) = await GetBytes(chunk, cancellationToken);

        // The chunk list's URL is what promised the digest; fall back to the redirect target when it has none.
        var (stream, _) = ChunkReader.OpenVerified(bytes, ChunkReader.DigestFromUri(chunk) is null ? finalUri : chunk);
        await using (stream)
        {
            await foreach (var package in ChunkReader.ReadPackages(stream, chunk, cancellationToken))
                yield return package;
        }
    }

    public async Task<byte[]> DownloadBlob(Uri blob, CancellationToken cancellationToken)
        => (await GetBytes(blob, cancellationToken)).Bytes;

    public Uri DownloadUri(ThunderstoreVersionRef version)
        => new($"{Host}/package/download/{version.Package.Namespace}/{version.Package.Name}/{version.Version}/");

    private static Uri PackageUri(ThunderstorePackageRef package)
        => new($"{Host}/api/experimental/package/{package.Namespace}/{package.Name}/");

    private async Task<T?> GetJsonOrNull<T>(Uri uri, CancellationToken cancellationToken) where T : class
    {
        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureSuccess(response, uri);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    private async Task<(byte[] Bytes, Uri FinalUri)> GetBytes(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, cancellationToken);
        EnsureSuccess(response, uri);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return (bytes, response.RequestMessage?.RequestUri ?? uri);
    }

    private static void EnsureSuccess(HttpResponseMessage response, Uri uri)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Thunderstore request {uri} failed with {(int)response.StatusCode} {response.StatusCode}", inner: null, response.StatusCode);
    }
}
