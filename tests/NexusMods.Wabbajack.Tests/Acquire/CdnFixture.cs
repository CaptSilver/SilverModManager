using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using NexusMods.Hashing.xxHash3;
using NexusMods.Sdk.Hashes;
using NexusMods.Wabbajack.Format;

namespace NexusMods.Wabbajack.Tests.Acquire;

/// <summary>A file split into parts the way the Wabbajack CDN serves it, and a handler that serves it.</summary>
internal sealed class CdnFixture
{
    public const int PartSize = 100;

    public byte[] Content { get; }
    public string OriginalFileName { get; }
    public Hash FileHash => Xx64Hasher.Hash(Content);
    public IReadOnlyList<byte[]> Parts { get; }

    public CdnFixture(int length = 250, string originalFileName = "Some Mod-123-1-0.7z")
    {
        Content = Enumerable.Range(0, length).Select(i => (byte)(i * 31 + 7)).ToArray();
        OriginalFileName = originalFileName;
        Parts = Content.Chunk(PartSize).ToArray();
    }

    public byte[] DefinitionGz()
    {
        var parts = Parts.Select((bytes, index) => new
        {
            Index = index,
            Offset = index * PartSize,
            Size = bytes.Length,
            Hash = WabbajackHash.ToBase64(Xx64Hasher.Hash(bytes)),
        });
        var json = JsonSerializer.Serialize(new
        {
            OriginalFileName,
            Size = Content.Length,
            Hash = WabbajackHash.ToBase64(FileHash),
            Parts = parts,
        });

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }
}

/// <summary>Serves a <see cref="CdnFixture"/> and records every request; <see cref="Respond"/> can override any of them.</summary>
internal sealed class CdnHandler(CdnFixture fixture) : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly List<(Uri Uri, string? Host)> _requests = [];

    public IReadOnlyList<(Uri Uri, string? Host)> Requests
    {
        get { lock (_lock) return _requests.ToArray(); }
    }

    public int RequestsFor(string pathSuffix) => Requests.Count(r => r.Uri.AbsolutePath.EndsWith(pathSuffix, StringComparison.Ordinal));

    /// <summary>Called with the request's path and the number of earlier requests for it; return null to serve the real bytes.</summary>
    public Func<string, int, HttpResponseMessage?>? Respond { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int earlier;
        lock (_lock)
        {
            earlier = _requests.Count(r => r.Uri.AbsolutePath == request.RequestUri!.AbsolutePath);
            _requests.Add((request.RequestUri!, request.Headers.Host));
        }

        var path = request.RequestUri!.AbsolutePath;
        var custom = Respond?.Invoke(path, earlier);
        if (custom is not null) return Task.FromResult(custom);

        if (path.EndsWith("/definition.json.gz", StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.DefinitionGz()) });

        var marker = path.LastIndexOf("/parts/", StringComparison.Ordinal);
        if (marker >= 0 && int.TryParse(path[(marker + "/parts/".Length)..], out var index) && index < fixture.Parts.Count)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fixture.Parts[index]) });

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
