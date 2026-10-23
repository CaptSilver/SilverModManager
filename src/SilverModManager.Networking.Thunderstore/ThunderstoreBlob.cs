using JetBrains.Annotations;
using NexusMods.Sdk.Hashes;

namespace NexusMods.Networking.Thunderstore;

/// <summary>
/// A content-addressed blob on Thunderstore's CDN: the final URI after redirects, and the SHA-256 of its bytes.
/// </summary>
[PublicAPI]
public readonly record struct ThunderstoreBlob(Uri Uri, Sha256Value Sha256);

/// <summary>
/// The package-listing index of a community: the blob itself and the chunk blobs it points to.
/// </summary>
[PublicAPI]
public sealed record ListingIndex(ThunderstoreBlob Index, IReadOnlyList<Uri> Chunks);

/// <summary>
/// A blob's bytes do not hash to the digest its content-addressed URL promises.
/// </summary>
[PublicAPI]
public sealed class ThunderstoreBlobIntegrityException(Uri uri, Sha256Value expected, Sha256Value actual)
    : Exception($"Blob {uri} should hash to {expected} but hashes to {actual}")
{
    public Uri Uri { get; } = uri;
    public Sha256Value Expected { get; } = expected;
    public Sha256Value Actual { get; } = actual;
}
