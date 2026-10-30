using System.Security.Cryptography;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Hashes;

/// <summary>
/// The one way src hashes a file on disk. The file is opened for a front-to-back read and handed to the hasher, which
/// asks for <see cref="HashingLimits.ReadBlockSize"/> bytes at a time, so there is no buffer size for a caller to get
/// wrong. A repo scan fails on any other call that feeds a file to a hasher.
/// </summary>
public static class FileHashing
{
    /// <summary>The file's xxHash3, the identity the file store and the game index use.</summary>
    public static ValueTask<Hash> XxHash3(AbsolutePath file, CancellationToken cancellationToken = default)
        => Digest<Hash, System.IO.Hashing.XxHash3, Xx3Hasher>(file, cancellationToken);

    /// <summary>The file's xxHash64.</summary>
    public static ValueTask<Hash> XxHash64(AbsolutePath file, CancellationToken cancellationToken = default)
        => Digest<Hash, System.IO.Hashing.XxHash64, Xx64Hasher>(file, cancellationToken);

    /// <summary>The file's SHA-256.</summary>
    public static ValueTask<Sha256Value> Sha256(AbsolutePath file, CancellationToken cancellationToken = default)
        => Digest<Sha256Value, SHA256, Sha256Hasher>(file, cancellationToken);

    /// <summary>The file's MD5.</summary>
    public static ValueTask<Md5Value> Md5(AbsolutePath file, CancellationToken cancellationToken = default)
        => Digest<Md5Value, MD5, Md5Hasher>(file, cancellationToken);

    /// <summary>Every digest of the file, computed in one read.</summary>
    public static ValueTask<MultiHash> HashAll(AbsolutePath file, CancellationToken cancellationToken = default)
        => Read(file, static async (stream, token) => await MultiHasher.HashStream(stream, token).ConfigureAwait(false), cancellationToken);

    /// <summary>The file's digest under any streaming hasher.</summary>
    public static ValueTask<THash> Digest<THash, TState, THasher>(AbsolutePath file, CancellationToken cancellationToken = default)
        where THash : unmanaged, IEquatable<THash>
        where THasher : IStreamingHasher<THash, TState, THasher>
        => Read(file, static (stream, token) => THasher.HashAsync(stream, cancellationToken: token), cancellationToken);

    /// <summary>
    /// Opens the file for a sequential read and lets <paramref name="hash"/> run a hasher over the stream, for the
    /// callers that combine hashers (a <see cref="MultiHasherBuilder"/>) or wrap the stream to report progress. The
    /// stream is closed when the callback returns.
    /// </summary>
    public static ValueTask<T> Read<T>(AbsolutePath file, Func<Stream, CancellationToken, ValueTask<T>> hash, CancellationToken cancellationToken = default)
        => Read(file, SequentialFileRead.Open, hash, cancellationToken);

    /// <summary>
    /// <see cref="Read{T}(AbsolutePath, Func{Stream, CancellationToken, ValueTask{T}}, CancellationToken)"/> with the
    /// stream supplied by <paramref name="open"/>, so a test can count or fault the reads.
    /// </summary>
    public static async ValueTask<T> Read<T>(AbsolutePath file, Func<AbsolutePath, Stream> open, Func<Stream, CancellationToken, ValueTask<T>> hash, CancellationToken cancellationToken = default)
    {
        await using var stream = open(file);
        return await hash(stream, cancellationToken).ConfigureAwait(false);
    }
}
