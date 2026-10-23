using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace NexusMods.Sdk.Hashes;

/// <summary>
/// Represents a SHA-256 value.
/// </summary>
[PublicAPI]
[JsonConverter(typeof(Sha256JsonConverter))]
public readonly struct Sha256Value : IEquatable<Sha256Value>
{
    internal const int Size = 32;
    internal const int HexStringSize = 64;
    private readonly InlineArray _array;

    /// <summary>
    /// Gets the value as a span.
    /// </summary>
    public ReadOnlySpan<byte> AsSpan() => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<InlineArray, byte>(ref Unsafe.AsRef(in _array)), length: Size);

    private Sha256Value(InlineArray array)
    {
        _array = array;
    }

    /// <summary>
    /// Creates a new value from a span.
    /// </summary>
    public static Sha256Value From(ReadOnlySpan<byte> input)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Length, Size, nameof(input));
        var slice = input[..Size];

        var array = new InlineArray();
        var span = array.AsSpan();
        slice.CopyTo(span);

        return new Sha256Value(array);
    }

    /// <summary>
    /// Creates a new value from a hex string.
    /// </summary>
    public static Sha256Value FromHex(ReadOnlySpan<char> input)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Length, HexStringSize, nameof(input));
        var slice = input[..(Size * 2)];

        var array = new InlineArray();
        var span = array.AsSpan();

        var status = Convert.FromHexString(slice, span, out _, out _);
        if (status != OperationStatus.Done) throw new ArgumentException($"Failed to convert from hex: status={status},input=`{input.ToString()}`", nameof(input));

        return new Sha256Value(array);
    }

    /// <summary>
    /// Parses exactly 64 hex characters (either case). Anything else, including null or empty input,
    /// yields false, so a malformed digest can never be mistaken for a value.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> input, out Sha256Value value)
    {
        value = default;
        if (input.Length != HexStringSize) return false;

        var array = new InlineArray();
        var status = Convert.FromHexString(input, array.AsSpan(), out _, out _);
        if (status != OperationStatus.Done) return false;

        value = new Sha256Value(array);
        return true;
    }

    /// <summary>
    /// Writes the value as a lowercase hex string to <paramref name="destination"/>.
    /// </summary>
    public int ToHex(Span<char> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, HexStringSize, nameof(destination));

        var success = Convert.TryToHexStringLower(AsSpan(), destination, out var charsWritten);
        Debug.Assert(success, "should always be true after destination length check");

        return charsWritten;
    }

    /// <inheritdoc/>
    public bool Equals(Sha256Value other) => other.AsSpan().SequenceEqual(AsSpan());

    /// <inheritdoc/>
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is Sha256Value other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => MemoryMarshal.Read<int>(AsSpan());

    /// <inheritdoc/>
    public override string ToString() => Convert.ToHexStringLower(AsSpan());

    [InlineArray(length: Size)]
    private struct InlineArray
    {
        private byte _value;

        public Span<byte> AsSpan() => MemoryMarshal.CreateSpan(ref Unsafe.As<InlineArray, byte>(ref this), length: Size);
    }

    /// <summary/>
    public static bool operator ==(Sha256Value left, Sha256Value right) => left.Equals(right);
    /// <summary/>
    public static bool operator !=(Sha256Value left, Sha256Value right) => !(left == right);
}

[PublicAPI]
public class Sha256Hasher : IStreamingHasher<Sha256Value, SHA256, Sha256Hasher>
{
    public static Sha256Value Hash(ReadOnlySpan<byte> input)
    {
        Span<byte> hashBytes = stackalloc byte[Sha256Value.Size];
        var didHash = SHA256.TryHashData(input, hashBytes, out var bytesWritten);
        Debug.Assert(bytesWritten == hashBytes.Length && didHash);

        return Sha256Value.From(hashBytes);
    }

    public static SHA256 Initialize() => SHA256.Create();

    public static SHA256 Update(SHA256 state, ReadOnlySpan<byte> input) => throw new NotSupportedException("SHA256 hasher doesn't support updates with spans");

    public static SHA256 Update(SHA256 state, byte[] input, int offset, int count)
    {
        state.TransformBlock(input, inputOffset: offset, inputCount: count, input, outputOffset: 0);
        return state;
    }

    public static Sha256Value Finish(SHA256 state)
    {
        _ = state.TransformFinalBlock([], inputCount: 0, inputOffset: 0);
        return Sha256Value.From(state.Hash);
    }

    public static ValueTask<Sha256Value> HashAsync(Stream stream, int bufferSize = IStreamingHasher<Sha256Value, SHA256, Sha256Hasher>.DefaultBufferSize, CancellationToken cancellationToken = default)
    {
        return StreamingHasher<Sha256Value, SHA256, Sha256Hasher>.HashAsync(stream, bufferSize, cancellationToken);
    }
}
