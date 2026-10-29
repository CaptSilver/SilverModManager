using System.Buffers;
using JetBrains.Annotations;

namespace SilverModManager.Sdk.Hashes;

/// <summary>
/// Represents a streaming hash algorithm.
/// </summary>
[PublicAPI]
public interface IStreamingHasher<THash, TState, TSelf> : IHasher<THash, TSelf>
    where THash : unmanaged, IEquatable<THash>
    where TSelf : IStreamingHasher<THash, TState, TSelf>
{
    public const int DefaultBufferSize = HashingLimits.ReadBlockSize;

    /// <summary>
    /// Hashes the stream contents until the end.
    /// </summary>
    static abstract ValueTask<THash> HashAsync(Stream stream, int bufferSize = DefaultBufferSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Initializes the streaming state.
    /// </summary>
    static abstract TState Initialize();

    /// <summary>
    /// Updates the streaming state.
    /// </summary>
    static abstract TState Update(TState state, ReadOnlySpan<byte> input);

    /// <summary>
    /// Updates the streaming state.
    /// </summary>
    static virtual TState Update(TState state, byte[] input, int offset, int count) => TSelf.Update(state, input.AsSpan(start: offset, length: count));

    /// <summary>
    /// Finalizes the streaming state.
    /// </summary>
    static abstract THash Finish(TState state);
}

[PublicAPI]
public static class StreamingHasher<THash, TState, THasher>
    where THash : unmanaged, IEquatable<THash>
    where THasher : IStreamingHasher<THash, TState, THasher>
{
    /// <inheritdoc cref="IStreamingHasher{THash,TState,TSelf}.HashAsync"/>
    public static async ValueTask<THash> HashAsync(
        Stream stream,
        int bufferSize,
        CancellationToken cancellationToken)
    {
        // A short stream does not need a full block; the pool hands out the large ones without a fresh allocation each file.
        if (stream.CanSeek) bufferSize = (int)Math.Clamp(stream.Length - stream.Position, 1, bufferSize);
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            var state = THasher.Initialize();
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0) break;

                state = THasher.Update(state, buffer, offset: 0, count: bytesRead);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return THasher.Finish(state);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
