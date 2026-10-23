using System.IO.Compression;
using System.Text.Json;
using NexusMods.Networking.Thunderstore.DTOs;
using NexusMods.Sdk.Hashes;

namespace NexusMods.Networking.Thunderstore;

/// <summary>
/// Reads Thunderstore's content-addressed listing blobs. Their body is gzip without a
/// <c>Content-Encoding</c> header, so the HTTP stack does not unwrap it, and the URL carries a
/// <c>sha256/&lt;hex&gt;</c> segment that the bytes must match.
/// </summary>
internal static class ChunkReader
{
    /// <summary>
    /// The digest a content-addressed URL promises, or null when the URL has no valid <c>sha256/&lt;hex&gt;</c> segment.
    /// The live CDN appends a storage suffix and extension to the digest (<c>sha256/&lt;hex&gt;.sh_xxx.blob</c>), so
    /// only the text before the first dot is the digest.
    /// </summary>
    public static Sha256Value? DigestFromUri(Uri uri)
    {
        var segments = uri.Segments;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].TrimEnd('/') != "sha256") continue;
            var next = segments[i + 1].TrimEnd('/');
            var dot = next.IndexOf('.');
            if (Sha256Value.TryParse(dot < 0 ? next : next[..dot], out var digest)) return digest;
        }
        return null;
    }

    /// <summary>
    /// Hashes <paramref name="bytes"/> and compares against the digest in <paramref name="origin"/>, if it has one.
    /// </summary>
    /// <returns>The computed digest.</returns>
    public static Sha256Value Verify(ReadOnlySpan<byte> bytes, Uri origin)
    {
        var actual = Sha256Hasher.Hash(bytes);
        var expected = DigestFromUri(origin);
        if (expected is { } e && e != actual) throw new ThunderstoreBlobIntegrityException(origin, e, actual);
        return actual;
    }

    /// <summary>
    /// Checks the body against the digest in <paramref name="origin"/>, then returns the gunzipped stream. The
    /// compressed body is buffered because the digest covers all of it and has to match before anything is parsed.
    /// </summary>
    public static async ValueTask<Stream> OpenVerified(Stream body, Uri origin, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, cancellationToken);
        return OpenVerified(buffer.ToArray(), origin).stream;
    }

    internal static (Stream stream, Sha256Value digest) OpenVerified(byte[] bytes, Uri origin)
    {
        var digest = Verify(bytes, origin);
        return (new GZipStream(new MemoryStream(bytes, writable: false), CompressionMode.Decompress), digest);
    }

    /// <summary>
    /// Streams the package records out of a gunzipped chunk without materialising the array. A malformed record
    /// surfaces as a <see cref="JsonException"/> that names <paramref name="origin"/>.
    /// </summary>
    public static async IAsyncEnumerable<PackageListing> ReadPackages(
        Stream gunzipped,
        Uri origin,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = JsonSerializer
            .DeserializeAsyncEnumerable<PackageListing>(gunzipped, ThunderstoreApi.JsonOptions, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (JsonException e)
            {
                throw new JsonException($"Malformed package record in chunk {origin}: {e.Message}", e);
            }

            if (!hasNext) yield break;
            if (enumerator.Current is { } package) yield return package;
        }
    }
}
