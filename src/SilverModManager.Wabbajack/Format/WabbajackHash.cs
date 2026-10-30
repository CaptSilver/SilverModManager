using System.Buffers.Binary;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Wabbajack.Format;

/// <summary>
/// Wabbajack writes an xxHash64 as base64 of its eight little-endian bytes. The app names the same bytes with a
/// <see cref="FileIdentity"/> of kind <see cref="HashKind.XxHash64"/>.
/// </summary>
public static class WabbajackHash
{
    private const int Length = sizeof(ulong);

    /// <exception cref="FormatException">The input is not base64 of exactly eight bytes.</exception>
    public static FileIdentity Parse(string base64)
    {
        Span<byte> bytes = stackalloc byte[Length + 4];
        if (!Convert.TryFromBase64String(base64, bytes, out var written) || written != Length)
            throw new FormatException($"'{base64}' is not the base64 of an eight byte hash");
        return FileIdentity.XxHash64(Hash.From(BinaryPrimitives.ReadUInt64LittleEndian(bytes)));
    }

    /// <exception cref="InvalidOperationException">The identity is not an xxHash64.</exception>
    public static string ToBase64(FileIdentity identity)
    {
        Span<byte> bytes = stackalloc byte[Length];
        if (!identity.TryXxHash64(out var hash))
            throw new InvalidOperationException($"{identity} is a {identity.Kind} identity, not an xxHash64");
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, hash.Value);
        return Convert.ToBase64String(bytes);
    }
}
